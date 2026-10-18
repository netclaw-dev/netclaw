// -----------------------------------------------------------------------
// <copyright file="OwnTemporaryFolderCorrectionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Issue #2554, Fix B: the short managed temporary folder is below <c>/tmp</c>.
/// The platform-temp advice (<see cref="TemporaryPathCorrectionPolicy"/>) must
/// not send the agent from its own temporary folder to the same folder. A path
/// elsewhere below <c>/tmp</c> still gets the advice. These tests check paths
/// only; they create no folder.
/// </summary>
public sealed class OwnTemporaryFolderCorrectionTests
{
    private const string Envelope = "/home/user/.netclaw/sessions/example";
    private const string Base = "/tmp/netclaw-1000";

    public static bool IsPosix => !OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "The short layout and /tmp advice use POSIX paths.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The short layout and /tmp advice use POSIX paths.")]
    [InlineData(FileWriteTool.ToolName)]
    [InlineData(FileEditTool.ToolName)]
    public void A_file_change_in_the_own_temporary_folder_gets_no_advice(string toolName)
    {
        var storage = Storage();
        var path = $"{Base}/{Key()}/p/report.md";

        var correction = Policy().EvaluateStructuredFileChange(
            new ToolName(toolName),
            ToolInput.Create("Path", path),
            Context(storage),
            new ToolPathPolicy(ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux), []));

        Assert.Null(correction);
    }

    [SlopwatchSuppress("SW001", "The short layout and /tmp advice use POSIX paths.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The short layout and /tmp advice use POSIX paths.")]
    public void A_file_change_elsewhere_in_tmp_gets_advice_that_names_the_short_folder()
    {
        var storage = Storage();

        var correction = Policy().EvaluateStructuredFileChange(
            new ToolName(FileWriteTool.ToolName),
            ToolInput.Create("Path", "/tmp/report.md"),
            Context(storage),
            new ToolPathPolicy(ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux), []));

        Assert.NotNull(correction);
        Assert.Equal($"{Base}/{Key()}/p", correction.Target.ManagedTemporaryDirectory);
        Assert.Equal("/tmp", correction.Target.PlatformTemporaryRoot);
    }

    [SlopwatchSuppress("SW001", "The short layout and /tmp advice use POSIX paths.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The short layout and /tmp advice use POSIX paths.")]
    public void A_file_change_in_the_temporary_folder_of_another_session_gets_advice()
    {
        var storage = Storage();

        var correction = Policy().EvaluateStructuredFileChange(
            new ToolName(FileWriteTool.ToolName),
            ToolInput.Create("Path", $"{Base}/0123456789abcdef/p/report.md"),
            Context(storage),
            new ToolPathPolicy(ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux), []));

        Assert.NotNull(correction);
    }

    private static SessionStoragePaths Storage()
        => SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Envelope),
            new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase(Base)));

    private static string Key()
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Envelope)))[..16];

    private static TemporaryPathCorrectionPolicy Policy()
        => new(ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux), "/tmp", new LexicalPathInspector());

    private static ToolInvocationContext Context(SessionStoragePaths storage)
        => TestToolExecutionContext.CreateBoundWithStorage(
            "signalr/example",
            storage,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            }).Invocation;

    /// <summary>Resolves each root to itself and sees no link, so only path text decides.</summary>
    private sealed class LexicalPathInspector : IPlatformTemporaryPathInspector
    {
        public bool TryResolveRoot(string path, ShellPathStyle pathStyle, out string resolvedRoot)
        {
            var created = CanonicalPath.TryCreate(path, relativeBase: null, pathStyle, out var root);
            resolvedRoot = created ? root.Value : string.Empty;
            return created;
        }

        public bool HasNoLinkEscape(string root, string path, ShellPathStyle pathStyle) => true;

        public bool SupportsPathInspection(ShellPathStyle pathStyle) => true;
    }
}
