// -----------------------------------------------------------------------
// <copyright file="PersonalReadReachMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// Kill list for the issue #2554 targets of the <c>path-access</c> Stryker gate
/// (<c>scripts/run-path-access-mutations.sh</c>):
/// <list type="bullet">
/// <item>Fix A, the Personal audience check: <see cref="Team_read_does_not_follow_a_wider_write_profile"/>.</item>
/// <item>Fix A, the Read and Attach operation check: <see cref="Project_declaration_does_not_follow_the_write_profile"/>
/// and <see cref="Write_decision_does_not_follow_the_read_profile"/>.</item>
/// <item>Fix A, the write mode check: <see cref="Personal_read_reaches_everywhere_only_for_write_all"/>.</item>
/// <item>Fix A, the root union: <see cref="Personal_read_and_attach_add_the_write_roots"/>.</item>
/// <item>Fix B, key folder versus base: <see cref="Key_folder_is_a_root_and_the_base_is_not"/>.</item>
/// <item>Review item S1, a configured root over the base: <see cref="A_root_over_the_base_does_not_reach_another_key_folder"/>.</item>
/// </list>
/// The folder checks of <c>ManagedTemporaryEnvironment.EnsureFolders</c> are in the
/// linked <c>ManagedTemporaryFolderCheckTests</c>.
/// </summary>
public sealed class PersonalReadReachMutationTests : IDisposable
{
    private readonly string _basePath = Path.Combine(
        Path.GetTempPath(),
        "netclaw-mutation-tests",
        Guid.NewGuid().ToString("N"));
    private readonly NetclawPaths _paths;
    private readonly string _readRoot;
    private readonly string _writeRoot;
    private readonly string _outside;

    public PersonalReadReachMutationTests()
    {
        _paths = new NetclawPaths(_basePath);
        _paths.EnsureDirectoriesExist();
        _readRoot = Path.Combine(_basePath, "read-root");
        _writeRoot = Path.Combine(_basePath, "write-root");
        _outside = Path.Combine(_basePath, "outside");
        Directory.CreateDirectory(_readRoot);
        Directory.CreateDirectory(_writeRoot);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Personal_read_reaches_everywhere_only_for_write_all(bool attach)
    {
        var operation = attach ? PathAccessPolicy.FileOperation.Attach : PathAccessPolicy.FileOperation.Read;
        var path = Path.Combine(_outside, "x.png");

        Assert.True(Allowed(Policy(TrustAudience.Personal, WriteAll()), TrustAudience.Personal, path, operation));
        Assert.False(Allowed(Policy(TrustAudience.Personal, Roots(_writeRoot)), TrustAudience.Personal, path, operation));
        Assert.False(Allowed(Policy(TrustAudience.Personal, None()), TrustAudience.Personal, path, operation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Personal_read_and_attach_add_the_write_roots(bool attach)
    {
        var operation = attach ? PathAccessPolicy.FileOperation.Attach : PathAccessPolicy.FileOperation.Read;
        var policy = Policy(TrustAudience.Personal, Roots(_writeRoot));
        var context = Context(TrustAudience.Personal, Deep());

        Assert.True(Allowed(policy, context, Path.Combine(_writeRoot, "x"), operation));
        Assert.True(Allowed(policy, context, Path.Combine(_readRoot, "x"), operation));
        Assert.Contains(_writeRoot, policy.GetTrustedRoots(context, operation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Team_read_does_not_follow_a_wider_write_profile(bool attach)
    {
        var operation = attach ? PathAccessPolicy.FileOperation.Attach : PathAccessPolicy.FileOperation.Read;
        var context = Context(TrustAudience.Team, Deep());

        Assert.False(Allowed(Policy(TrustAudience.Team, Roots(_writeRoot)), context, Path.Combine(_writeRoot, "x"), operation));
        Assert.False(Allowed(Policy(TrustAudience.Team, WriteAll()), context, Path.Combine(_outside, "x"), operation));
        Assert.DoesNotContain(_writeRoot, Policy(TrustAudience.Team, Roots(_writeRoot)).GetTrustedRoots(context, operation));
    }

    [Fact]
    public void Project_declaration_does_not_follow_the_write_profile()
    {
        var context = Context(TrustAudience.Personal, Deep());

        Assert.False(Allowed(Policy(TrustAudience.Personal, WriteAll()), context, _outside, PathAccessPolicy.FileOperation.DeclareProjectScope));
        Assert.False(Allowed(Policy(TrustAudience.Personal, Roots(_writeRoot)), context, _writeRoot, PathAccessPolicy.FileOperation.DeclareProjectScope));
    }

    [Fact]
    public void Write_decision_does_not_follow_the_read_profile()
    {
        // The rule is one-way: read covers write, never write covers read.
        var policy = Policy(TrustAudience.Personal, Roots(_writeRoot));

        Assert.False(Allowed(policy, TrustAudience.Personal, Path.Combine(_readRoot, "x"), PathAccessPolicy.FileOperation.Write));
    }

    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Public)]
    public void Key_folder_is_a_root_and_the_base_is_not(TrustAudience audience)
    {
        if (OperatingSystem.IsWindows())
            return;

        var temporaryBase = Path.Combine(_basePath, "netclaw-base");
        var layout = new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase(temporaryBase));
        var own = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "a")), layout);
        var other = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "b")), layout);
        var policy = Policy(audience, Roots(_writeRoot));
        var context = Context(audience, own);

        Assert.True(Allowed(policy, context, Path.Combine(own.ManagedTemporary.Directory.Value, "x"), PathAccessPolicy.FileOperation.Read));
        Assert.True(Allowed(policy, context, Path.Combine(own.ManagedTemporary.Directory.Value, "x"), PathAccessPolicy.FileOperation.Write));
        Assert.False(Allowed(policy, context, Path.Combine(other.ManagedTemporary.Directory.Value, "x"), PathAccessPolicy.FileOperation.Read));
        Assert.False(Allowed(policy, context, Path.Combine(temporaryBase, "x"), PathAccessPolicy.FileOperation.Read));
    }

    // Review item S1: a configured root over the base reaches the base and the own key folder,
    // never the key folder of another session. The deny compares without case and the own key
    // folder compares with case, so a distinct case variant fails closed.
    [Theory]
    [InlineData(TrustAudience.Team, "mixed-hex", "/synthetic-netclaw-case-fixture/0")]
    [InlineData(TrustAudience.Public, "mixed-hex", "/synthetic-netclaw-case-fixture/0")]
    [InlineData(TrustAudience.Team, "all-digits", "/synthetic-netclaw-case-fixture/1825")]
    [InlineData(TrustAudience.Public, "all-digits", "/synthetic-netclaw-case-fixture/1825")]
    public void A_root_over_the_base_does_not_reach_another_key_folder(
        TrustAudience audience, string keyShape, string envelopeRoot)
    {
        if (OperatingSystem.IsWindows())
            return;

        var temporaryBase = Path.Combine(_basePath, "netclaw-base");
        var layout = new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase(temporaryBase));
        // These virtual roots supply deterministic hash input. The constructors perform no file I/O.
        var own = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(envelopeRoot), layout);
        var other = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot("/synthetic-netclaw-case-fixture/other"), layout);
        var config = new ToolConfig();
        var profile = audience == TrustAudience.Team ? config.AudienceProfiles.Team : config.AudienceProfiles.Public;
        profile.ReadFiles = Roots(_basePath);
        var policy = new PathAccessPolicy(config, _paths, new ToolPathPolicy([]));
        var context = Context(audience, own);
        var read = PathAccessPolicy.FileOperation.Read;
        var ownKey = Path.GetFileName(own.ManagedTemporary.StorageRoot.Value);
        var otherKey = Path.GetFileName(other.ManagedTemporary.StorageRoot.Value);
        var allDigits = keyShape == "all-digits";
        var uppercaseOwnKey = ownKey.ToUpperInvariant();

        Assert.Matches(allDigits ? "^[0-9]{16}$" : "^(?=.*[0-9])(?=.*[a-f])[0-9a-f]{16}$", ownKey);
        Assert.Equal(allDigits, string.Equals(ownKey, uppercaseOwnKey, StringComparison.Ordinal));
        Assert.False(string.Equals(ownKey, otherKey, StringComparison.OrdinalIgnoreCase));

        Assert.True(Allowed(policy, context, Path.Combine(own.ManagedTemporary.Directory.Value, "x"), read));
        Assert.True(Allowed(policy, context, temporaryBase, read));
        Assert.True(Allowed(policy, context, Path.Combine(_basePath, "NETCLAW-BASE"), read));
        Assert.False(Allowed(policy, context, Path.Combine(other.ManagedTemporary.Directory.Value, "x"), read));
        Assert.False(Allowed(policy, context, Path.Combine(_basePath, "NETCLAW-BASE", otherKey, "p", "x"), read));
        Assert.Equal(allDigits, Allowed(policy, context, Path.Combine(temporaryBase, uppercaseOwnKey, "p", "x"), read));
    }

    private SessionStoragePaths Deep()
        => SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "current")),
            ManagedTemporaryLayout.InsideSessionStorage.Instance);

    private PathAccessPolicy Policy(TrustAudience audience, ToolFilesystemAccessProfile write)
    {
        var config = new ToolConfig();
        var profile = audience switch
        {
            TrustAudience.Personal => config.AudienceProfiles.Personal,
            TrustAudience.Team => config.AudienceProfiles.Team,
            _ => config.AudienceProfiles.Public
        };
        profile.ReadFiles = Roots(_readRoot);
        profile.AttachFiles = Roots(_readRoot);
        profile.WriteFiles = write;
        return new PathAccessPolicy(config, _paths, new ToolPathPolicy([]));
    }

    private bool Allowed(PathAccessPolicy policy, TrustAudience audience, string path, PathAccessPolicy.FileOperation operation)
        => Allowed(policy, Context(audience, Deep()), path, operation);

    private static bool Allowed(PathAccessPolicy policy, ToolInvocationContext context, string path, PathAccessPolicy.FileOperation operation)
        => policy.Evaluate(path, context, operation) is PathAccessPolicy.PathAccessDecision.Allowed;

    private static ToolFilesystemAccessProfile Roots(string root)
        => new() { Mode = ToolFilesystemMode.Roots, Roots = [root] };

    private static ToolFilesystemAccessProfile WriteAll()
        => new() { Mode = ToolFilesystemMode.All };

    private static ToolFilesystemAccessProfile None()
        => new() { Mode = ToolFilesystemMode.None };

    private static ToolInvocationContext Context(TrustAudience audience, SessionStoragePaths storage) =>
        new(
            new ToolRunScope
            {
                Session = new ToolSessionScope.Bound("signalr/current", storage),
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                InlineOutputBudget = InlineOutputBudget.Default,
                InteractiveApproval = new InteractiveApprovalCapability.Unavailable()
            },
            ToolExecutionTimeout.Default);
}
