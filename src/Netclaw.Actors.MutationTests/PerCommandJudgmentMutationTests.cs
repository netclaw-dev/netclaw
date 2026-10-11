// -----------------------------------------------------------------------
// <copyright file="PerCommandJudgmentMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Authorization;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// Proves the boundary of per-command judgment (approval taxonomy PR 5). An
/// unresolved command is one exact candidate. Only owner decision D1 covers it:
/// an unknown operand only, and a safe phrase or a grant for "anywhere". A
/// folder or chat grant, an unknown redirect target or directory, and a glob
/// scope keep the prompt. An unattended call gets the same decision (D2); it is
/// denied where a chat would prompt.
/// </summary>
public sealed class PerCommandJudgmentMutationTests : IDisposable
{
    private const string UnknownOperand = "kubectl get pods -l \"app=$(whoami)\"";
    private const string UnknownRedirect = "kubectl get pods > \"$(whoami).log\"";

    private readonly NetclawPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "netclaw-per-command-mutations", Guid.NewGuid().ToString("N")));
    private readonly SessionStoragePaths _storage;
    private readonly string _workingDirectory;

    public PerCommandJudgmentMutationTests()
    {
        _paths.EnsureDirectoriesExist();
        _storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "current")));
        Directory.CreateDirectory(_storage.SessionDirectory.Value);
        _workingDirectory = Path.Combine(_paths.BasePath, "work");
        Directory.CreateDirectory(_workingDirectory);
    }

    public void Dispose() => Directory.Delete(_paths.BasePath, recursive: true);

    public enum GrantKind
    {
        Everywhere,
        Folder,
        Chat,
    }

    // D1 grants: only a grant for "anywhere" covers an unknown operand.
    [Theory]
    [InlineData(GrantKind.Everywhere, true)]
    [InlineData(GrantKind.Folder, false)]
    [InlineData(GrantKind.Chat, false)]
    public async Task Only_a_grant_for_anywhere_covers_an_unknown_operand(GrantKind kind, bool allowed)
    {
        if (OperatingSystem.IsWindows())
            return;

        var decision = await AuthorizeAsync(
            UnknownOperand,
            interactive: true,
            grants: new Dictionary<string, GrantKind> { ["kubectl get pods"] = kind, ["whoami"] = kind });

        if (allowed)
        {
            Assert.IsType<AuthorizationDecision.Allowed>(decision);
            return;
        }

        AssertExactPrompt(decision, UnknownOperand);
    }

    // D2: D1 applies to an unattended call too. A grant for anywhere covers it;
    // a folder grant leaves the exact prompt, which nobody can answer.
    [Theory]
    [InlineData(GrantKind.Everywhere, true)]
    [InlineData(GrantKind.Folder, false)]
    public async Task An_unattended_call_gets_the_decision_of_a_chat(GrantKind kind, bool allowed)
    {
        if (OperatingSystem.IsWindows())
            return;

        var decision = await AuthorizeAsync(
            UnknownOperand,
            interactive: false,
            grants: new Dictionary<string, GrantKind> { ["kubectl get pods"] = kind, ["whoami"] = kind });

        if (allowed)
        {
            Assert.IsType<AuthorizationDecision.Allowed>(decision);
            return;
        }

        Assert.Equal(ToolAuthorizer.UnattendedApprovalRequired, Assert.IsType<AuthorizationDecision.Denied>(decision).Reason);
    }

    // An unknown redirect target, a glob scope, and a command after an unproved
    // directory change are exact as a whole. Even a grant for "anywhere" on the
    // exact text does not cover them.
    [Theory]
    [InlineData(UnknownRedirect, UnknownRedirect)]
    [InlineData("cat src/*/notes.txt", "cat src/*/notes.txt")]
    [InlineData("pushd /tmp && cat notes.txt", "cat notes.txt")]
    [InlineData("cat lnk/../notes.txt", "cat lnk/../notes.txt")]
    public async Task An_exact_command_keeps_exact_consent(string command, string exact)
    {
        if (OperatingSystem.IsWindows())
            return;

        // lnk leaves the working directory, so ".." after it has no lexical scope.
        var target = Path.Combine(_paths.BasePath, "elsewhere", "deep");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(Path.Combine(_workingDirectory, "lnk"), target);

        var decision = await AuthorizeAsync(
            command,
            interactive: true,
            grants: new Dictionary<string, GrantKind>
            {
                [exact] = GrantKind.Everywhere,
                ["cat"] = GrantKind.Everywhere,
                ["kubectl get pods"] = GrantKind.Everywhere,
                ["pushd"] = GrantKind.Everywhere,
                ["whoami"] = GrantKind.Everywhere
            });

        AssertExactPrompt(decision, exact);
    }

    // D1 safe phrases: a reviewed phrase covers an unknown operand in an
    // interactive call, but not an unknown redirect target or a glob scope.
    [Theory]
    [InlineData("cat \"$(whoami)\"", true)]
    [InlineData("cat notes.txt > \"$(whoami).log\"", false)]
    [InlineData("cat src/*/notes.txt", false)]
    public async Task A_safe_phrase_covers_only_an_unknown_operand(string command, bool allowed)
    {
        if (OperatingSystem.IsWindows())
            return;

        var decision = await AuthorizeAsync(
            command,
            interactive: true,
            grants: new Dictionary<string, GrantKind>(),
            safePhrases: ["cat", "whoami"]);

        Assert.Equal(allowed, decision is AuthorizationDecision.Allowed);
    }

    // D1 skips only the unknown path value. With reads confined to the working
    // directory, a known path outside it still needs a prompt.
    [Theory]
    [InlineData("cat \"notes/$(whoami).txt\"", true)]
    [InlineData("cat \"notes/$(whoami).txt\" /usr/share/netclaw-absent.txt", false)]
    public async Task A_safe_phrase_still_checks_each_known_path(string command, bool allowed)
    {
        if (OperatingSystem.IsWindows())
            return;

        var decision = await AuthorizeAsync(
            command,
            interactive: true,
            grants: new Dictionary<string, GrantKind>(),
            safePhrases: ["cat", "whoami"],
            readRoots: [_workingDirectory]);

        Assert.Equal(allowed, decision is AuthorizationDecision.Allowed);
    }

    // A source that the parser cannot split keeps one exact answer for the call.
    [Fact]
    public async Task An_unparseable_source_keeps_one_exact_answer()
    {
        if (OperatingSystem.IsWindows())
            return;

        var decision = await AuthorizeAsync(
            "while read -r f; do cat \"$f\"; done < list.txt",
            interactive: true,
            grants: new Dictionary<string, GrantKind> { ["cat"] = GrantKind.Everywhere });

        var consent = Assert.IsType<AuthorizationDecision.NeedsConsent>(decision);
        Assert.True(consent.Request.IsMessy);
        Assert.Equal(
            [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            consent.Request.Options.Select(static option => option.Key.Value));
    }

    // The authorizer gives a prompt with no candidate its full command text, so
    // the decision does not show this fact. A source with no command candidate
    // must stay unresolved for the rules before that point (advice, twins).
    [Fact]
    public void A_source_without_command_candidates_stays_unresolved()
    {
        const string command = "while read -r f; do cat \"$f\"; done < list.txt";
        var matcher = new ShellApprovalMatcher(ShellExecutionEnvironmentDefaults.Bash);
        var approval = matcher.AnalyzeInvocation(
            new ToolName(ShellTool.ToolName),
            new Dictionary<string, object?> { ["Command"] = command, ["WorkingDirectory"] = "/work/project" });
        Assert.True(approval.IsMessy);
        Assert.Empty(approval.Candidates);
        Assert.Empty(approval.CommandCandidates);

        Assert.Same(approval, ToolAccessPolicy.WithCommandCandidates(approval));
    }

    private static void AssertExactPrompt(AuthorizationDecision decision, string exact)
    {
        var consent = Assert.IsType<AuthorizationDecision.NeedsConsent>(decision);
        Assert.Contains(exact, consent.Request.CandidateVerbs);
        Assert.Equal(
            [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            consent.Request.Options.Select(static option => option.Key.Value));
    }

    private Task<AuthorizationDecision> AuthorizeAsync(
        string command,
        bool interactive,
        IReadOnlyDictionary<string, GrantKind> grants,
        IReadOnlyList<string>? safePhrases = null,
        IReadOnlyList<string>? readRoots = null)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        if (readRoots is not null)
        {
            config.AudienceProfiles.GlobalReadRoots = [];
            config.AudienceProfiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
            {
                Mode = ToolFilesystemMode.Roots,
                Roots = [.. readRoots]
            };
        }

        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Approval,
            ToolOverrides = new() { [ShellTool.ToolName] = ToolApprovalMode.Approval }
        };
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var policy = new ToolAccessPolicy(
            _paths,
            config,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(environment),
            new ToolPathPolicy(environment, [_paths.ConfigDirectory]),
            safeVerbs: SafeVerbList.FromVerbs(ApprovalShell.Bash, safePhrases ?? []));
        var registry = new ToolRegistry();
        registry.Register(new ShellProbeTool());
        var executor = new DispatchingToolExecutor(registry, policy, new ScopedGrantService(grants, _workingDirectory));
        return executor.Authorizer.AuthorizeAsync(
            new FunctionCallContent(
                "per-command-call",
                ShellTool.ToolName,
                new Dictionary<string, object?> { ["Command"] = command, ["WorkingDirectory"] = _workingDirectory }),
            new ToolExecutionContext(
                new ToolRunScope
                {
                    Session = new ToolSessionScope.Bound("signalr/per-command-mutation", _storage),
                    Audience = TrustAudience.Personal,
                    Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
                    InlineOutputBudget = InlineOutputBudget.Default,
                    InteractiveApproval = interactive
                        ? new InteractiveApprovalCapability.Available(new UnexpectedConsentBridge())
                        : new InteractiveApprovalCapability.Unavailable()
                },
                ToolExecutionTimeout.Default),
            CancellationToken.None);
    }

    // A store that covers a candidate when its words, or its exact text, have a
    // grant of the given kind. A folder grant names the working directory.
    private sealed class ScopedGrantService(
        IReadOnlyDictionary<string, GrantKind> grants,
        string folder) : IToolApprovalService, IShellApprovalMatchService
    {
        public Task<ShellApprovalMatchResult> MatchShellCandidatesAsync(
            ShellApprovalMatchRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(ShellApprovalMatchResult.Create(
                request.Candidates,
                persistentStoreFailure: null,
                request.Candidates.Select(Match).ToArray()));

        private ShellGrantCandidateResult Match(ShellGrantCandidate candidate)
        {
            var key = candidate.Candidate.VerbTokens is { } tokens
                ? string.Join(' ', tokens)
                : candidate.Candidate.Verb;
            if (!grants.TryGetValue(key, out var kind) && !grants.TryGetValue(candidate.Candidate.Verb, out kind))
                return ShellGrantCandidateResult.Uncovered(candidate);

            // A stored shell grant needs command words. Only a chat grant can
            // name the exact text of a command without words.
            if (kind == GrantKind.Chat || candidate.Candidate.VerbTokens is not { } words)
                return ShellGrantCandidateResult.Session(candidate);

            return ShellGrantCandidateResult.Persistent(
                candidate,
                ApprovalEntry.CreateTokenPrefix(
                    ApprovalShell.Bash,
                    words,
                    directory: kind == GrantKind.Folder ? folder : null,
                    createdAt: null));
        }

        public Task<ToolApprovalCheckResult> CheckApprovalAsync(
            ToolApprovalSessionId? sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<ApprovalCandidate> candidates,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("A shell call uses the per-candidate match.");

        public Task RecordApprovalCandidatesAsync(
            ToolApprovalSessionId sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<ToolApprovalGrant> grants,
            CancellationToken ct = default)
            => throw new InvalidOperationException("These tests do not record grants.");
    }

    private sealed class ShellProbeTool : INetclawTool
    {
        private readonly AIFunction _function = AIFunctionFactory.Create((string Command) => Command, ShellTool.ToolName);
        public string Name => ShellTool.ToolName;
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
        public string Description => "Never runs.";
        public string GrantCategory => "shell";
        public System.Text.Json.JsonElement ParameterSchema => _function.JsonSchema;
        public AITool ToAITool() => _function;

        public Task<string> ExecuteAsync(
            IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct)
            => throw new InvalidOperationException("The authorizer must not run the tool.");
    }

    private sealed class UnexpectedConsentBridge : IParentConsentBridge
    {
        public Task<ConsentStep> RequestConsentAsync(ParentApprovalRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("The authorizer must not request consent.");
    }
}
