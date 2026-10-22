// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryRootAccessTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Issue #2554, Fix B: the managed temporary key folder (<c>&lt;base&gt;/&lt;key&gt;</c>)
/// is a session root for every audience. The shared base is never a root, so a
/// session cannot reach the key folder of another session. The file is linked
/// into <c>Netclaw.Actors.MutationTests</c> for the <c>path-access</c> Stryker gate.
/// </summary>
public sealed class ManagedTemporaryRootAccessTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly string _base;
    private readonly SessionStoragePaths _storage;
    private readonly SessionStoragePaths _otherStorage;

    public ManagedTemporaryRootAccessTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _base = Path.Combine(_dir.Path, "netclaw-base");
        var layout = new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase(_base));
        _storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "session-a")),
            layout);
        _otherStorage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "session-b")),
            layout);
    }

    public void Dispose() => _dir.Dispose();

    // PathAccessPolicy.FileOperation is internal, so a public theory cannot take it.
    public enum Operation
    {
        Read,
        Write,
        Attach
    }

    private static PathAccessPolicy.FileOperation ToFileOperation(Operation operation) => operation switch
    {
        Operation.Write => PathAccessPolicy.FileOperation.Write,
        Operation.Attach => PathAccessPolicy.FileOperation.Attach,
        _ => PathAccessPolicy.FileOperation.Read
    };

    public static TheoryData<TrustAudience, Operation> AudienceOperations => new()
    {
        { TrustAudience.Team, Operation.Read },
        { TrustAudience.Team, Operation.Write },
        { TrustAudience.Team, Operation.Attach },
        { TrustAudience.Public, Operation.Read },
        { TrustAudience.Public, Operation.Write },
        { TrustAudience.Personal, Operation.Read },
        { TrustAudience.Personal, Operation.Write },
    };

    [Theory]
    [MemberData(nameof(AudienceOperations))]
    public void A_session_uses_its_own_temporary_folder(TrustAudience audience, Operation operation)
    {
        var policy = CreatePolicy();
        var path = Path.Combine(KeyFolder(_storage), "p", "render", "frame.png");

        Assert.IsType<PathAccessPolicy.PathAccessDecision.Allowed>(
            policy.Evaluate(path, Context(audience, _storage), ToFileOperation(operation)));
    }

    [Theory]
    [MemberData(nameof(AudienceOperations))]
    public void A_session_cannot_use_the_temporary_folder_of_another_session(
        TrustAudience audience,
        Operation operation)
    {
        var policy = CreatePolicy();
        var path = Path.Combine(KeyFolder(_otherStorage), "p", "frame.png");

        Assert.IsType<PathAccessPolicy.PathAccessDecision.Denied>(
            policy.Evaluate(path, Context(audience, _storage), ToFileOperation(operation)));
    }

    [Theory]
    [MemberData(nameof(AudienceOperations))]
    public void A_session_cannot_use_the_shared_base(TrustAudience audience, Operation operation)
    {
        var policy = CreatePolicy();

        Assert.IsType<PathAccessPolicy.PathAccessDecision.Denied>(
            policy.Evaluate(Path.Combine(_base, "planted.txt"), Context(audience, _storage), ToFileOperation(operation)));
    }

    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Public)]
    [InlineData(TrustAudience.Personal)]
    public void Trusted_roots_hold_the_key_folder_and_not_the_base(TrustAudience audience)
    {
        var roots = CreatePolicy().GetTrustedRoots(Context(audience, _storage), PathAccessPolicy.FileOperation.Read);

        Assert.Contains(KeyFolder(_storage), roots);
        Assert.DoesNotContain(_base, roots);
        Assert.DoesNotContain(KeyFolder(_otherStorage), roots);
    }

    [Fact]
    public void A_child_run_reads_the_temporary_folder_of_its_parent()
    {
        // The parent and its children share the key folder, as they share the envelope.
        var child = _storage.ForChild(new SubAgentRunId("run-7"), new SubAgentScopeId("s/subagent/a/run-7"));
        var path = Path.Combine(KeyFolder(_storage), "p", "handoff.json");

        Assert.IsType<PathAccessPolicy.PathAccessDecision.Allowed>(
            CreatePolicy().Evaluate(path, Context(TrustAudience.Team, child), PathAccessPolicy.FileOperation.Read));
    }

    // The expected key folder, from the session-cwd spec, not from the code under test.
    private string KeyFolder(SessionStoragePaths storage)
        => Path.Combine(
            _base,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(storage.Binding!.EnvelopeRoot.Value)))[..16]);

    // Personal gets bounded Roots profiles here, so that only session roots decide.
    private PathAccessPolicy CreatePolicy()
    {
        var config = new ToolConfig();
        var outside = Path.Combine(_dir.Path, "configured-root");
        foreach (var profile in config.AudienceProfiles.GetAllProfiles())
        {
            profile.ReadFiles = new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.Roots, Roots = [outside] };
            profile.WriteFiles = new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.Roots, Roots = [outside] };
            profile.AttachFiles = new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.Roots, Roots = [outside] };
        }

        return new PathAccessPolicy(config, _paths, new ToolPathPolicy([]));
    }

    private static ToolInvocationContext Context(TrustAudience audience, SessionStoragePaths storage)
        => TestToolExecutionContext.CreateBoundWithStorage(
            "signalr/session-a",
            storage,
            new TestToolExecutionContextOptions
            {
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true),
                ProjectDirectory = null,
                ChannelType = "signalr"
            }).Invocation;
}
