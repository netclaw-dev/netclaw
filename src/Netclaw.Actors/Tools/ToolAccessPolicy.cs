// -----------------------------------------------------------------------
// <copyright file="ToolAccessPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Applies the configured tool, path, shell, and approval policies to one tool invocation.
/// </summary>
public sealed class ToolAccessPolicy
{
    private enum ApprovalOptionProfile
    {
        OneShotOnly,
        Standard,
        StandardWithDirectory,
        McpTool
    }

    private static readonly IReadOnlyList<ToolApprovalOption> ManagedTemporaryRetryOptions =
        Array.AsReadOnly<ToolApprovalOption>(
        [
            new(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
        ]);

    private readonly ToolConfig _toolConfig;
    private readonly EffectivePolicyDefaults _defaults;
    private readonly ToolAudienceProfileResolver _profileResolver;
    private readonly ShellCommandPolicy _shellCommandPolicy;
    private readonly ToolPathPolicy _toolPathPolicy;
    private readonly ShellApprovalMatcher _shellApprovalMatcher;
    private readonly PathAccessPolicy _pathAccessPolicy;
    private readonly IToolApprovalMatcher _fileApprovalMatcher;
    private readonly FeatureGates _featureGates;
    private readonly ReviewedSafeShellPolicy? _safeVerbPolicy;
    private readonly TemporaryPathCorrectionPolicy _temporaryPathCorrectionPolicy;

    internal ApprovalShell Shell => _shellCommandPolicy.Environment.Grammar == ShellGrammar.Bash
        ? ApprovalShell.Bash
        : ApprovalShell.PowerShell;

    internal ShellExecutionEnvironment ShellEnvironment => _shellCommandPolicy.Environment;

    internal ToolConfig ToolConfig => _toolConfig;

    internal ToolPathPolicy ProtectedPathPolicy => _toolPathPolicy;

    internal ShellCommandPolicy ShellCommandPolicy => _shellCommandPolicy;

    internal PathAccessPolicy SharedPathAccessPolicy => _pathAccessPolicy;

    public ToolAccessPolicy(
        NetclawPaths paths,
        ToolConfig toolConfig,
        EffectivePolicyDefaults defaults,
        ShellCommandPolicy shellCommandPolicy,
        ToolPathPolicy toolPathPolicy,
        IToolApprovalMatcher? fileApprovalMatcher = null,
        FeatureGates? featureGates = null,
        SafeVerbList? safeVerbs = null)
        : this(
            paths,
            toolConfig,
            defaults,
            shellCommandPolicy,
            toolPathPolicy,
            TemporaryPathCorrectionPolicy.Create(shellCommandPolicy.Environment),
            fileApprovalMatcher,
            featureGates,
            safeVerbs)
    {
    }

    internal ToolAccessPolicy(
        NetclawPaths paths,
        ToolConfig toolConfig,
        EffectivePolicyDefaults defaults,
        ShellCommandPolicy shellCommandPolicy,
        ToolPathPolicy toolPathPolicy,
        TemporaryPathCorrectionPolicy platformTemporaryScopePolicy,
        IToolApprovalMatcher? fileApprovalMatcher = null,
        FeatureGates? featureGates = null,
        SafeVerbList? safeVerbs = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // shellCommandPolicy (deny-list) and toolPathPolicy (protected paths) are
        // required security controls — non-nullable so a caller cannot omit them.
        // The shell gate below dereferences them directly, so a stray null fails
        // loudly at the point of use rather than silently skipping a check.
        _toolConfig = toolConfig;
        _defaults = defaults;
        _profileResolver = new ToolAudienceProfileResolver(toolConfig);
        _shellCommandPolicy = shellCommandPolicy;
        _toolPathPolicy = toolPathPolicy;
        if (!ReferenceEquals(shellCommandPolicy.Environment, toolPathPolicy.Environment))
        {
            throw new ArgumentException(
                "Shell command and path policies must use the same shell environment.",
                nameof(toolPathPolicy));
        }

        _shellApprovalMatcher = new ShellApprovalMatcher(shellCommandPolicy.Environment);
        _pathAccessPolicy = new PathAccessPolicy(
            toolConfig,
            paths,
            toolPathPolicy);
        _fileApprovalMatcher = fileApprovalMatcher ?? DefaultApprovalMatcher.Instance;
        _featureGates = featureGates ?? FeatureGates.AllEnabled;
        _safeVerbPolicy = safeVerbs is null
            ? null
            : new ReviewedSafeShellPolicy(safeVerbs, _pathAccessPolicy);
        _temporaryPathCorrectionPolicy = platformTemporaryScopePolicy;
    }

    /// <summary>
    /// Filters the tools that the model can see for a turn. The trust context
    /// is required. A caller without a resolved audience must refuse tool
    /// exposure itself. This method does not treat a missing audience as Public.
    /// </summary>
    public IReadOnlyList<AITool> FilterExposedTools(
        IEnumerable<AITool> tools,
        ToolRegistry registry,
        EffectiveTrustContext trustContext)
        => tools
            .Where(tool =>
            {
                var name = GetToolName(tool);
                if (name is null)
                    return true;

                var registration = registry.GetRegistrationByToolName(name);
                return registration is null || IsToolExposed(registration, trustContext);
            })
            .ToList();

    public IReadOnlyList<INetclawTool> FilterDiscoverableTools(
        IEnumerable<INetclawTool> tools,
        ToolInvocationContext context)
        => tools.Where(tool => IsToolExposed(tool, context)).ToList();

    public bool IsToolExposed(ToolRegistration registration, EffectiveTrustContext trustContext)
        => IsToolExposed(registration.Tool, trustContext.EffectiveAudience);

    public bool IsToolExposed(INetclawTool tool, ToolInvocationContext context)
        => IsToolExposed(tool, ResolveAudience(context));

    public bool IsMcpServerExposed(McpServerName serverName, TrustAudience audience)
        => _profileResolver.IsMcpServerAllowed(serverName, audience);

    internal bool IsToolExposed(INetclawTool tool, TrustAudience audience)
    {
        // Feature-disabled tools are hidden for ALL audiences
        if (IsFeatureDisabledTool(tool.Name))
            return false;

        if (tool is McpToolAdapter mcp)
            return _profileResolver.IsMcpServerAllowed(new McpServerName(mcp.ServerName), audience)
                && _profileResolver.IsMcpToolAllowed(
                    new McpServerName(mcp.ServerName),
                    new ToolName(mcp.BareToolName),
                    audience)
                && _profileResolver.ResolveProfile(audience).ApprovalPolicy?.GetEffectiveMode(mcp.Name)
                    != ToolApprovalMode.Deny;

        if (!_profileResolver.IsToolAllowed(new ToolName(tool.Name), audience))
            return false;

        if (_profileResolver.ResolveProfile(audience).ApprovalPolicy?.GetEffectiveMode(tool.Name)
            == ToolApprovalMode.Deny)
        {
            return false;
        }

        if (IsShellCoupledTool(tool))
            return ResolveShellMode() == ShellExecutionMode.HostAllowed && audience == TrustAudience.Personal;

        return true;
    }

    /// <summary>
    /// Decides whether the caller's audience may use the tool. An MCP tool needs
    /// its server and the tool in the audience allow lists. Another tool needs its
    /// name in the audience profile.
    /// </summary>
    /// <returns>A denial, or null when the audience may use the tool.</returns>
    internal ToolAuthorizationDecision? AdmitAudience(INetclawTool tool, ToolExecutionContext context)
    {
        if (tool is McpToolAdapter mcp)
            return AdmitMcpAudience(mcp, context);

        return _profileResolver.IsToolAllowed(new ToolName(tool.Name), context.Invocation)
            ? null
            : ToolAuthorizationDecision.Deny("tool_not_allowed_for_audience_profile");
    }

    /// <summary>
    /// Converts the result of the shell screens and the consent mode into the
    /// preflight result that correction and coverage selection read.
    /// </summary>
    /// <param name="decision">The screen denial, the automatic allow, or the consent request.</param>
    /// <param name="analysis">The command analysis, or null when the call has no command text or a screen denied it.</param>
    /// <param name="directoryScopes">The directory proof that supplied the candidates, or null.</param>
    internal static ShellPolicyPreflightResult CompleteShellPreflight(
        ToolAuthorizationDecision decision,
        ShellCommandAnalysis? analysis,
        BashDirectoryScopeProjection? directoryScopes)
    {
        if (!decision.NeedsApproval)
        {
            return new ShellPolicyPreflightResult.Complete(
                decision,
                decision.Outcome == ToolAuthorizationOutcome.Allowed ? analysis : null);
        }

        if (analysis is null)
            return new ShellPolicyPreflightResult.Complete(decision, authorizedAnalysis: null);

        return decision.ApprovalContext is { } approvalContext
            ? new ShellPolicyPreflightResult.Continue(
                analysis,
                approvalContext,
                directoryScopes)
            : new ShellPolicyPreflightResult.Complete(
                ToolAuthorizationDecision.Deny("internal_policy_failure"),
                authorizedAnalysis: null);
    }

    private ToolAuthorizationDecision? AdmitMcpAudience(
        McpToolAdapter tool,
        ToolExecutionContext context)
    {
        var serverName = new McpServerName(tool.ServerName);
        if (!_profileResolver.IsMcpServerAllowed(serverName, context.Invocation))
            return ToolAuthorizationDecision.Deny("mcp_server_not_allowed_for_audience_profile");

        if (!_profileResolver.IsMcpToolAllowed(
                serverName,
                new ToolName(tool.BareToolName),
                context.Invocation))
        {
            return ToolAuthorizationDecision.Deny("mcp_tool_not_allowed_for_audience_profile");
        }

        return null;
    }

    /// <summary>
    /// Returns the arguments that consent reads. An MCP call drops the Netclaw
    /// metadata fields, so a grant never depends on them.
    /// </summary>
    internal static IDictionary<string, object?>? GetApprovalArguments(
        INetclawTool tool,
        IDictionary<string, object?>? arguments)
    {
        if (tool is not McpToolAdapter)
            return arguments;

        var (_, approvalArguments) = ToolCallMeta.ExtractFrom(
            arguments,
            key => ToolArgumentValidator.ResolveMetaField(tool, key));
        return approvalArguments;
    }

    /// <summary>Selects the matcher that gives the candidates and the mode key of a call that is not a shell call.</summary>
    internal IToolApprovalMatcher SelectApprovalMatcher(INetclawTool tool)
        => tool is McpToolAdapter
            ? McpApprovalMatcher.Instance
            : SelectMatcherForTool(new ToolName(tool.Name));

    /// <summary>Returns the directory that ShellTool executes in, from the argument or the context.</summary>
    /// <remarks>
    /// All shell policy checks use this directory. The explicit tool argument can
    /// be absent while the context supplies an active project, session, or
    /// inherited directory.
    /// </remarks>
    internal static string? ResolveShellWorkingDirectory(
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments)
        => context.ResolveShellCwd(ExtractWorkingDirectory(arguments));

    /// <summary>Applies the hard-deny list to the parsed command.</summary>
    /// <returns>A denial that names the deny category, or null.</returns>
    internal ToolAuthorizationDecision? ScreenHardDeny(ShellCommandAnalysis analysis)
    {
        var hardDenyDecision = _shellCommandPolicy.Evaluate(analysis);
        if (!hardDenyDecision.Allowed)
            return ToolAuthorizationDecision.Deny(
                $"hard_deny_{hardDenyDecision.DenyCategory?.ToWireName() ?? "unknown"}");

        return null;
    }

    /// <summary>Denies shell text that names a protected path, whatever the operation.</summary>
    internal ToolAuthorizationDecision? ScreenProtectedShellText(ShellCommandAnalysis analysis)
        => _toolPathPolicy.CommandReferencesDeniedPath(analysis)
            ? ToolAuthorizationDecision.Deny("shell_references_protected_path")
            : null;

    /// <summary>Denies a working directory with a <c>..</c> segment.</summary>
    /// <remarks>The OS resolves ".." after a symlink. Lexical policy normalization does not.</remarks>
    internal static ToolAuthorizationDecision? ScreenShellWorkingDirectory(string? workingDirectory)
        => workingDirectory is not null && CanonicalPath.HasParentSegment(workingDirectory)
            ? ToolAuthorizationDecision.Deny("shell_invalid_working_directory")
            : null;

    /// <summary>Projects the parsed command to its consent candidates in the resolved working directory.</summary>
    internal ShellApprovalAnalysis AnalyzeShellApproval(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        string? workingDirectory,
        ShellCommandAnalysis analysis)
        => _shellApprovalMatcher.AnalyzeInvocation(
            toolName,
            WithResolvedShellWorkingDirectory(arguments, workingDirectory),
            analysis);

    /// <summary>
    /// Returns true when a complete directory proof gives each occurrence of an
    /// unresolved compound its own directory.
    /// </summary>
    /// <remarks>
    /// A complete directory proof clears the unresolved-input gate. Attended
    /// and unattended calls use the same proof (D2). The caller must screen
    /// each slice before it uses the proof candidates.
    /// </remarks>
    internal bool TryProveDirectoryScopes(
        ShellCommandAnalysis analysis,
        ShellApprovalAnalysis approval,
        [NotNullWhen(true)] out BashDirectoryScopeProjection? proof)
    {
        proof = null;
        if (approval is not { IsMessy: true, Candidates.Count: 0 }
            || !BashDirectoryScopeProjection.TryCreate(
                analysis,
                _shellCommandPolicy,
                _shellApprovalMatcher,
                out var projection)
            || !projection.Slices.All(slice => IsDirectoryScopeEligible(slice.WorkingDirectory)))
        {
            return false;
        }

        proof = projection;
        return true;
    }

    /// <summary>Screens each slice of a directory proof in slice order.</summary>
    /// <returns>The first denial of a slice: hard deny, protected text, or file protection.</returns>
    internal ToolAuthorizationDecision? ScreenDirectoryScopes(
        BashDirectoryScopeProjection proof,
        ToolExecutionContext context)
    {
        foreach (var slice in proof.Slices)
        {
            var denial = ScreenHardDeny(slice.Analysis)
                ?? ScreenProtectedShellText(slice.Analysis)
                ?? ScreenShellTrustZone(slice.Analysis, slice.WorkingDirectory, context);
            if (denial is not null)
                return denial;
        }

        return null;
    }

    /// <summary>
    /// Gives a call with an unresolved command the candidates of each command.
    /// The unresolved command becomes one exact candidate, so the other
    /// commands get their normal decisions. Attended and unattended calls get
    /// the same candidates (D2).
    /// </summary>
    internal static ShellApprovalAnalysis WithCommandCandidates(ShellApprovalAnalysis approval)
        => approval is { IsMessy: true, Candidates.Count: 0, CommandCandidates.Count: > 0 }
            ? approval with
            {
                Candidates = approval.CommandCandidates,
                IsMessy = false
            }
            : approval;

    /// <summary>Replaces the unresolved candidates with the candidates of a screened directory proof.</summary>
    internal static ShellApprovalAnalysis WithDirectoryScopes(
        ShellApprovalAnalysis approval,
        BashDirectoryScopeProjection proof)
        => approval with
        {
            Candidates = proof.Candidates,
            IsMessy = false
        };

    /// <summary>Resolves the consent mode of a shell call. Auto becomes Approval for a link-following tree effect.</summary>
    internal ToolApprovalMode GetShellApprovalMode(
        ToolName toolName,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments,
        ShellCommandAnalysis? analysis)
        => ResolveShellApprovalMode(
            GetApprovalMode(toolName, context, arguments, _shellApprovalMatcher),
            analysis?.RequiresExactTreeApproval == true);

    /// <summary>Returns the denial for a Deny consent mode or an unknown mode, or null.</summary>
    internal static ToolAuthorizationDecision? ScreenApprovalModeDenial(ToolApprovalMode mode)
        => GetApprovalModeDecision(mode) is { Outcome: ToolAuthorizationOutcome.Denied } denial
            ? denial
            : null;

    internal ToolAuthorizationDecision AuthorizeBackgroundJobControl(ToolExecutionContext context)
        => EvaluateShellCapability(context.Invocation)
           ?? ToolAuthorizationDecision.Allow(ToolAllowReason.BackgroundJobLifecycle);

    internal ToolAuthorizationDecision? EvaluateShellCapability(ToolInvocationContext context)
    {
        var shellMode = ResolveShellMode();
        if (shellMode == ShellExecutionMode.Off)
            return ToolAuthorizationDecision.Deny("shell_disabled");

        if (shellMode == ShellExecutionMode.SandboxOnly)
            return ToolAuthorizationDecision.Deny("shell_requires_sandbox_backend");

        return ResolveAudience(context) == TrustAudience.Personal
            ? null
            : ToolAuthorizationDecision.Deny("shell_requires_personal_context");
    }

    internal bool IsReviewedSafeCandidate(
        ShellPolicyCandidate candidate,
        ShellPolicyCandidatePathFacts pathFacts,
        ToolInvocationContext context)
        => _safeVerbPolicy is not null
           && _safeVerbPolicy.ShortCircuits(
               candidate,
               pathFacts,
               context);

    internal bool IsReviewedSafeIntentCandidate(
        ShellPolicyCandidate candidate,
        ShellPolicyCandidatePathFacts pathFacts,
        ToolInvocationContext context)
        => _safeVerbPolicy is not null
           && _safeVerbPolicy.ShortCircuitsCausalIntent(
               candidate,
               pathFacts,
               context);

    /// <summary>
    /// Returns true when a canonical Bash scope directory has no link from the
    /// volume root, except the platform temporary alias (R7).
    /// </summary>
    private bool IsDirectoryScopeEligible(string scopeDirectory)
        => ShellEnvironment.Grammar == ShellGrammar.Bash
           && CanonicalPath.TryCreate(scopeDirectory, relativeBase: null, ShellEnvironment.PathStyle, out var directory)
           && string.Equals(directory.Value, scopeDirectory, StringComparison.Ordinal)
           && FileSystemAuthority.IsLinkFreeFromVolumeRoot(directory, LinkRule.FromVolumeRootExceptTemporaryAlias);

    internal ShellApprovalMatcher ShellApprovalMatcher => _shellApprovalMatcher;

    /// <summary>
    /// Requires the working directory and every known path of the command to be
    /// inside a trusted root for Write.
    /// </summary>
    internal ToolAuthorizationDecision? ScreenShellTrustZone(
        ShellCommandAnalysis analysis,
        string? workingDirectory,
        ToolExecutionContext context)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var expandedWorkingDirectory = PathUtility.ExpandAndNormalize(workingDirectory, workingDirectory: null);
            if (expandedWorkingDirectory is null)
                return ToolAuthorizationDecision.Deny("shell_invalid_working_directory");

            var workingDirectoryAccess = _pathAccessPolicy.Evaluate(
                expandedWorkingDirectory,
                context.Invocation,
                PathAccessPolicy.FileOperation.Write);
            if (workingDirectoryAccess is not PathAccessPolicy.PathAccessDecision.Allowed)
                return ToolAuthorizationDecision.Deny("shell_working_directory_outside_trust_zone");
        }

        var readOnly = ReadOnlyOccurrences(analysis);
        return EnforceKnownShellPaths(
            ShellPolicyPathFacts.CreateExecutionViews(analysis)
                .SelectMany((view, index) => EnumerateKnownShellPaths(
                    view,
                    readOnly.Contains(analysis.Commands[index]))),
            context.Invocation);
    }

    /// <summary>
    /// Applies file protection to all paths in a shell policy projection: write
    /// protection, and read protection for a read-only program (decision D6).
    /// </summary>
    /// <remarks>
    /// A causal list adds an intent view after shell preflight.
    /// The coordinator must call this method before it checks stored grants or reviewed-safe coverage.
    /// </remarks>
    /// <param name="candidates">The path facts of each candidate, with its source occurrence.</param>
    /// <param name="analysis">The analysis that owns the source occurrences.</param>
    /// <param name="context">The invocation that supplies the trusted roots.</param>
    internal ToolAuthorizationDecision? EnforceProjectedShellFileProtection(
        IReadOnlyList<(ShellPolicyCandidatePathFacts PathFacts, CommandOccurrence? Occurrence)> candidates,
        ShellCommandAnalysis? analysis,
        ToolInvocationContext context)
    {
        var readOnly = analysis is null ? [] : ReadOnlyOccurrences(analysis);
        return EnforceKnownShellPaths(
            candidates.SelectMany(candidate => EnumerateKnownShellPaths(
                candidate.PathFacts,
                candidate.Occurrence is { } occurrence && readOnly.Contains(occurrence))),
            context);
    }

    /// <summary>
    /// Returns the occurrences of a Bash program that only reads its operands
    /// (<see cref="ShellVerbPolicyData.ReadOnlyOperandVerbs"/>, decision D6).
    /// </summary>
    /// <remarks>
    /// SECURITY: the set is empty for unresolved or dynamic syntax, because a
    /// function or an alias can replace the program. An occurrence with an
    /// assignment prefix or a dynamic program word is never read-only. A redirect
    /// that writes keeps write protection for its target only. Each argument must have a bounded value, so a
    /// glob or an unknown value cannot read a path that no check sees.
    /// </remarks>
    private static HashSet<CommandOccurrence> ReadOnlyOccurrences(ShellCommandAnalysis analysis)
    {
        var occurrences = new HashSet<CommandOccurrence>(ReferenceEqualityComparer.Instance);
        if (analysis.Environment.Grammar != ShellGrammar.Bash
            || !analysis.IsResolved
            || analysis.HasDynamicSyntax)
        {
            return occurrences;
        }

        foreach (var occurrence in analysis.Commands)
        {
            var directory = occurrence.WorkingDirectory is ShellValueDomain.Exact exact
                ? exact.Value
                : analysis.WorkingDirectory;
            if (occurrence is { IsComplete: true, Assignments.Count: 0, Clause.Verb: { IsDynamic: false, Tokens: [var program, ..] } }
                && ShellVerbPolicyData.ReadOnlyOperandVerbs.Contains(program)
                && occurrence.Arguments.All(argument => IsReadOnlyOperand(argument, directory)))
            {
                occurrences.Add(occurrence);
            }
        }

        return occurrences;
    }

    // SECURITY: each value must be bounded. A path gets a path fact. A plain word
    // must have one value that names no entry of the occurrence directory: such a
    // word (grep -r token config) is a path that no path fact sees, and its scope
    // is not read below, so the occurrence is not read-only. The parser reports a
    // brace word ({netclaw,secrets}.json) or an ANSI-C quoted word ($'\x73') as one
    // exact value, but Bash expands it, so such a word is never a read-only operand.
    private static bool IsReadOnlyOperand(AnalyzedArgument argument, string? directory)
        => !ToolPathPolicy.HasUnmodeledExpansion(argument.Element.Raw)
           && argument.Value is ShellValueDomain.Exact or ShellValueDomain.FiniteSet
           && (argument.Argument.IsPath
               || argument.Value is ShellValueDomain.Exact exact && !NamesEntry(exact.Value, directory));

    // The one shared file-word rule of the approval matcher, the store, and the doctor.
    private static bool NamesEntry(string word, string? directory)
        => ShellGrantFileWords.NamesEntry(word, directory, out _);

    private ToolAuthorizationDecision? EnforceKnownShellPaths(
        IEnumerable<ShellPathAccess> paths,
        ToolInvocationContext context)
    {
        foreach (var access in paths
                     .Where(static access => !ShellRedirectPolicyFacts.IsNullDevice(access.Path))
                     .DistinctBy(static access => (access.Path.Style, access.Path.Value, access.Read)))
        {
            if (IsShellPathAllowed(access, context))
                continue;

            // The code only names the cause. Both codes deny, and no grant opens either one.
            // shell_path_protected: the path or its link target is protected.
            // shell_path_outside_trusted_roots: a bounded (Roots) profile does not hold the path.
            return ToolAuthorizationDecision.Deny(
                access.Path.IsHostStyle && _toolPathPolicy.FileSystem.IsProtected(access.Path.Value, PathOperation.Write)
                    ? "shell_path_protected"
                    : "shell_path_outside_trusted_roots");
        }

        return null;
    }

    // D6: a read-only program can read a write-protected path that a file tool
    // may read. Its scopes and operands get read protection instead.
    private bool IsShellPathAllowed(ShellPathAccess access, ToolInvocationContext context)
        => _pathAccessPolicy.EvaluateShellPath(access.Path, context) is PathAccessPolicy.PathAccessDecision.Allowed
           || access.Read != ShellPathRead.None
           && _pathAccessPolicy.EvaluateShellReadPath(access.Path, context, access.Read == ShellPathRead.Operand)
               is PathAccessPolicy.PathAccessDecision.Allowed;

    private static IEnumerable<ShellPathAccess> EnumerateKnownShellPaths(
        ShellPolicyCandidatePathFacts candidate,
        bool readOnly)
    {
        if (candidate.RealScope.Path is { } realScope)
            yield return new ShellPathAccess(realScope, readOnly ? ShellPathRead.Scope : ShellPathRead.None);

        foreach (var path in EnumerateKnownShellPaths(candidate.Real, readOnly))
            yield return path;

        if (candidate.Intent is { } intent)
        {
            foreach (var path in EnumerateKnownShellPaths(intent, readOnly))
                yield return path;
        }
    }

    private static IEnumerable<ShellPathAccess> EnumerateKnownShellPaths(
        ShellPolicyResolvedPathView view,
        bool readOnly)
    {
        if (view.ResolutionBase.Path is { } resolutionBase)
            yield return new ShellPathAccess(resolutionBase, readOnly ? ShellPathRead.Scope : ShellPathRead.None);

        foreach (var fact in view.Facts.Where(static fact => fact.State == ShellPolicyPathResolutionState.Known))
        {
            // A redirect that writes (cat cfg > /tmp/x) keeps write protection for its target.
            var read = readOnly
                       && (fact.Source.Origin != ShellPolicyPathOrigin.Redirect
                           || fact.Source.RedirectMode == FileRedirectMode.Input)
                ? ShellPathRead.Operand
                : ShellPathRead.None;
            foreach (var path in fact.Paths)
                yield return new ShellPathAccess(path, read);
        }
    }

    private enum ShellPathRead
    {
        /// <summary>The path gets write protection only.</summary>
        None,

        /// <summary>A scope of a read-only program: the working directory or a candidate folder.</summary>
        Scope,

        /// <summary>A path that a read-only program reads.</summary>
        Operand,
    }

    private readonly record struct ShellPathAccess(CanonicalPath Path, ShellPathRead Read);

    internal ToolAuthorizationDecision? PreflightStructuredPathAccess(
        INetclawTool tool,
        ToolInvocationContext context,
        IDictionary<string, object?>? arguments)
    {
        if (!string.Equals(tool.GrantCategory, "file", StringComparison.Ordinal))
            return null;

        var request = tool.Name switch
        {
            FileReadTool.ToolName or FileListTool.ToolName =>
                (Argument: "Path", Operation: PathAccessPolicy.FileOperation.Read),
            FileSearchTool.ToolName =>
                (Argument: "Root", Operation: PathAccessPolicy.FileOperation.Read),
            FileWriteTool.ToolName or FileEditTool.ToolName =>
                (Argument: "Path", Operation: PathAccessPolicy.FileOperation.Write),
            AttachFileTool.ToolName =>
                (Argument: "Path", Operation: PathAccessPolicy.FileOperation.Attach),
            SetWorkingDirectoryTool.ToolName =>
                (Argument: "Path", Operation: PathAccessPolicy.FileOperation.DeclareProjectScope),
            _ => default
        };

        if (request.Argument is null)
            return ToolAuthorizationDecision.Deny("path_access_descriptor_missing");

        var rawPath = ToolArgumentHelper.GetString(arguments, request.Argument);
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;

        var decision = _pathAccessPolicy.Evaluate(rawPath, context, request.Operation);
        return decision is PathAccessPolicy.PathAccessDecision.Denied
        { Failure: PathAccessPolicy.PathAccessFailure.AccessDenied } denied
            ? ToolAuthorizationDecision.Deny("path_access_denied", denied.Error)
            : null;
    }

    internal static string? ExtractShellCommand(IDictionary<string, object?>? arguments)
    {
        // Use the shared extractor so JsonElement-valued arguments (the
        // shape LLM-generated tool calls arrive in) get string-converted
        // correctly. The direct `is string` pattern previously here
        // silently returned null for every real shell call, which disabled
        // the hard-deny screen. The matcher's
        // GetCommand uses ToolArgumentHelper.GetString — mirror it here
        // for consistency.
        if (arguments is null)
            return null;

        return ToolArgumentHelper.GetString(arguments, "Command")
            ?? ToolArgumentHelper.GetString(arguments, "command");
    }

    private static string? ExtractWorkingDirectory(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return null;

        return ToolArgumentHelper.GetString(arguments, "WorkingDirectory");
    }

    internal static IDictionary<string, object?>? WithResolvedShellWorkingDirectory(
        IDictionary<string, object?>? arguments,
        string? resolvedWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(resolvedWorkingDirectory)
            || !string.IsNullOrWhiteSpace(ExtractWorkingDirectory(arguments)))
        {
            return arguments;
        }

        var analysisArguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (arguments is not null)
        {
            foreach (var (key, value) in arguments)
                analysisArguments[key] = value;
        }

        analysisArguments["WorkingDirectory"] = resolvedWorkingDirectory;
        return analysisArguments;
    }

    /// <summary>
    /// Builds the consent request of a call that is not a shell call: its
    /// candidates, display text, offered options, and any temporary-directory advice.
    /// </summary>
    internal ToolAuthorizationDecision BuildNonShellConsentRequest(
        ToolName toolName,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments,
        IToolApprovalMatcher matcher)
    {
        var patterns = matcher.ExtractPatterns(toolName, arguments);
        var candidates = matcher.ExtractCandidates(toolName, arguments);
        var displayText = matcher.FormatForDisplay(toolName, arguments);
        var isMessy = matcher.IsMessy(toolName, arguments);
        var correction = _temporaryPathCorrectionPolicy.EvaluateStructuredFileChange(
            toolName,
            arguments,
            context.Invocation,
            _toolPathPolicy);
        return BuildApprovalDecision(
            toolName,
            context,
            patterns,
            candidates,
            displayText,
            isMessy,
            correction,
            hasReusablePhrase: true,
            directoryApprovalAvailable: false);
    }

    internal ToolAuthorizationDecision AuthorizeShellApproval(
        ToolName toolName,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments,
        ToolApprovalMode mode,
        ShellApprovalAnalysis? analysis,
        string? workingDirectory)
    {
        var approvalModeDecision = GetApprovalModeDecision(mode);
        if (approvalModeDecision is not null)
            return approvalModeDecision;

        context.Approval.SetCwd(workingDirectory);
        analysis ??= _shellApprovalMatcher.AnalyzeInvocation(
            toolName,
            WithResolvedShellWorkingDirectory(arguments, workingDirectory));
        return BuildApprovalDecision(
            toolName,
            context,
            analysis.Patterns,
            analysis.Candidates,
            analysis.DisplayText,
            analysis.IsMessy,
            correction: null,
            analysis.Candidates.All(HasReusableShellPhrase),
            IsShellDirectoryApprovalAvailable(
                analysis.Candidates,
                context.Approval.Cwd,
                GetSessionOwnedApprovalDirectories(context),
                ShellEnvironment.PathStyle));
    }

    private ToolAuthorizationDecision BuildApprovalDecision(
        ToolName toolName,
        ToolExecutionContext context,
        IReadOnlyList<string> patterns,
        IReadOnlyList<ApprovalCandidate> candidates,
        string displayText,
        bool isMessy,
        ToolCorrection? correction,
        bool hasReusablePhrase,
        bool directoryApprovalAvailable)
    {
        var candidateVerbs = candidates
            .Select(static candidate => candidate.Verb)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var managedTemporaryRetry = context.Approval.ManagedTemporaryRetry;
        var isManagedTemporaryRetry = managedTemporaryRetry is not null;
        var repository = isManagedTemporaryRetry
            ? null
            : ResolveOfferedRepository(toolName, isMessy, hasReusablePhrase, candidates, context.Approval.Cwd);
        IReadOnlyList<ToolApprovalOption> options;
        if (isManagedTemporaryRetry)
        {
            options = ManagedTemporaryRetryOptions;
        }
        else
        {
            options = BuildApprovalOptions(
                GetApprovalOptionProfile(
                    toolName,
                    isMessy,
                    hasReusablePhrase,
                    directoryApprovalAvailable),
                repository is not null,
                HasAssignmentDigest(candidates));
        }

        var approvalContext = new ToolApprovalContext(
            toolName.Value,
            displayText,
            patterns,
            candidateVerbs,
            options,
            Cwd: context.Approval.Cwd,
            IsMessy: isMessy,
            Candidates: candidates)
        {
            IsManagedTemporaryRetry = isManagedTemporaryRetry,
            ManagedTemporaryDirectory = managedTemporaryRetry?.ManagedTemporaryDirectory,
            PlatformTemporaryRoot = managedTemporaryRetry?.PlatformTemporaryRoot,
            RepositoryCommonDirectory = repository
        };

        return ToolAuthorizationDecision.RequiresApproval(
            approvalContext,
            isManagedTemporaryRetry ? null : correction);
    }

    internal ToolCorrection? EvaluateShellTemporaryCorrection(
        ShellCommandAnalysis analysis,
        IReadOnlyList<ApprovalCandidate> candidates,
        IDictionary<string, object?>? arguments,
        ToolInvocationContext invocation)
    {
        var correction = _temporaryPathCorrectionPolicy.Evaluate(analysis, candidates, arguments, invocation);
        if (correction is null)
            return null;

        if (_safeVerbPolicy is null)
            return correction;

        // Diagnostic classification suppresses relocation advice only. Normal authorization still owns execution and path access.
        if (_safeVerbPolicy.IsReviewedDiagnosticInvocation(candidates, analysis.Environment.PathStyle))
            return null;

        return correction;
    }

    internal ToolApprovalMode GetApprovalMode(
        ToolName toolName,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments,
        IToolApprovalMatcher matcher)
    {
        var audience = ResolveAudience(context.Invocation);
        var profile = ToolAudienceProfileDefaults.GetResolvedProfile(_toolConfig.AudienceProfiles, audience);
        var approvalModeKey = matcher.GetApprovalModeKey(toolName, arguments);
        return ResolveApprovalMode(
            profile.ApprovalPolicy,
            approvalModeKey,
            toolName,
            arguments,
            audience,
            matcher);
    }

    private static ToolAuthorizationDecision? GetApprovalModeDecision(ToolApprovalMode mode)
        => mode switch
        {
            ToolApprovalMode.Approval => null,
            ToolApprovalMode.Auto => ToolAuthorizationDecision.Allow(ToolAllowReason.PolicyAuto),
            ToolApprovalMode.Deny => ToolAuthorizationDecision.Deny("tool_denied_by_approval_policy"),
            _ => ToolAuthorizationDecision.Deny("internal_policy_failure")
        };

    /// <summary>
    /// Converts an interactive Auto request to one exact approval when a tree
    /// effect can follow links. The earlier path gate denies headless use.
    /// </summary>
    internal static ToolApprovalMode ResolveShellApprovalMode(
        ToolApprovalMode configuredMode,
        bool requiresExactTreeApproval)
        => configuredMode == ToolApprovalMode.Auto && requiresExactTreeApproval
            ? ToolApprovalMode.Approval
            : configuredMode;

    internal static ToolApprovalContext NarrowShellApprovalContext(
        ToolApprovalContext context,
        IReadOnlyList<ApprovalCandidate> unapprovedCandidates,
        IReadOnlyCollection<string> sessionOwnedDirectories,
        ShellPathStyle pathStyle)
    {
        var candidateVerbs = unapprovedCandidates
            .Select(static candidate => candidate.Verb)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        string? repository = null;
        IReadOnlyList<ToolApprovalOption> options;
        if (context.IsManagedTemporaryRetry)
        {
            options = ManagedTemporaryRetryOptions;
        }
        else
        {
            var shellToolName = new ToolName(ShellTool.ToolName);
            var hasReusablePhrase = unapprovedCandidates.All(HasReusableShellPhrase);
            repository = ResolveOfferedRepository(
                shellToolName, isMessy: false, hasReusablePhrase,
                unapprovedCandidates, context.Cwd);
            options = BuildApprovalOptions(
                GetApprovalOptionProfile(
                    shellToolName,
                    isMessy: false,
                    hasReusablePhrase,
                    IsShellDirectoryApprovalAvailable(
                        unapprovedCandidates,
                        context.Cwd,
                        sessionOwnedDirectories,
                        pathStyle)),
                repository is not null,
                HasAssignmentDigest(unapprovedCandidates));
        }

        return context with
        {
            Patterns = candidateVerbs,
            CandidateVerbs = candidateVerbs,
            Candidates = unapprovedCandidates,
            Options = options,
            RepositoryCommonDirectory = repository
        };
    }

    /// <summary>
    /// Returns true when every candidate's effective directory is one of the
    /// current session's named storage directories. Persisting an "Always
    /// here" grant scoped to one of those directories is dead-on-arrival because
    /// the next session has different paths. The button is hidden in that case so
    /// operators can pick "This chat" (the equivalent in-session
    /// semantics) or "Always anywhere" (folder-agnostic) instead.
    /// </summary>
    private static bool AllCandidatesResolveToSessionOwnedDirectory(
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        IReadOnlyCollection<string> sessionOwnedDirectories)
    {
        if (sessionOwnedDirectories.Count == 0 || candidates.Count == 0)
            return false;

        foreach (var candidate in candidates)
        {
            var effective = candidate.Directory ?? cwd;
            if (string.IsNullOrEmpty(effective))
                return false;

            if (!sessionOwnedDirectories.Any(directory =>
                    PathUtility.AreEquivalentPaths(effective, directory)))
                return false;
        }

        return true;
    }

    internal static IReadOnlyCollection<string> GetSessionOwnedApprovalDirectories(
        ToolExecutionContext context)
    {
        if (context.SessionStorage is not { } storage)
            return context.SessionDirectory is { Length: > 0 } sessionDirectory
                ? [sessionDirectory]
                : [];

        return new[]
            {
                storage.SessionDirectory.Value,
                storage.ManagedTemporary.Directory.Value,
                storage.ArtifactDirectory.Value,
                storage.WorktreeDirectory.Value
            }
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsShellDirectoryApprovalAvailable(
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        IReadOnlyCollection<string> sessionOwnedDirectories,
        ShellPathStyle pathStyle)
    {
        if (IsCwdTooShallow(cwd, pathStyle))
            return false;

        if (AllCandidatesResolveToSessionOwnedDirectory(candidates, cwd, sessionOwnedDirectories))
            return false;

        return true;
    }

    private static ApprovalOptionProfile GetApprovalOptionProfile(
        ToolName toolName,
        bool isMessy,
        bool hasReusablePhraseForEveryCandidate,
        bool includeDirectory)
    {
        if (isMessy || !hasReusablePhraseForEveryCandidate)
            return ApprovalOptionProfile.OneShotOnly;

        if (toolName.IsMcp)
            return ApprovalOptionProfile.McpTool;

        return includeDirectory
            ? ApprovalOptionProfile.StandardWithDirectory
            : ApprovalOptionProfile.Standard;
    }

    /// <summary>
    /// Builds the prompt's button row. The five-button default
    /// (Once / This chat / Always here / Always anywhere / Deny) is pruned
    /// in three cases:
    /// <list type="bullet">
    /// <item><b>Messy commands</b> (bash control-flow / unbalanced
    /// quotes/brackets) — only <c>Once</c> and <c>Deny</c> are offered.
    /// Persistence is impossible because the matcher cannot extract a verb
    /// chain to remember.</item>
    /// <item><b>Shallow cwd</b> (path depth fails the minimum-scope check) —
    /// <c>Always here</c> is omitted so an operator cannot accidentally write
    /// a folder-scoped grant for a too-shallow root like <c>/etc/</c>.
    /// <c>This chat</c> and <c>Always anywhere</c> remain available.</item>
    /// <item><b>Session-owned effective directory</b> (every candidate's
    /// effective directory is one of the current session's named storage
    /// directories) —
    /// <c>Always here</c> is omitted because the saved grant would be scoped
    /// to a directory that won't recur. <c>This chat</c> already provides
    /// the equivalent in-session semantics without polluting the persistent
    /// store.</item>
    /// <item><b>No directory scope</b> (all non-shell tools) — <c>Always
    /// here</c> is omitted because these matchers grant independently of cwd.
    /// For MCP tools, the remaining persistent choice is labeled <c>Always
    /// allow this tool</c> because it persists a canonical-tool grant.</item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<ToolApprovalOption> BuildApprovalOptions(
        ApprovalOptionProfile profile,
        bool includeRepository,
        bool hasAssignmentDigest)
    {
        if (profile is ApprovalOptionProfile.OneShotOnly)
        {
            return
            [
                new ToolApprovalOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolApprovalOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ];
        }

        var options = new List<ToolApprovalOption>(6)
        {
            new ToolApprovalOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new ToolApprovalOption(
                hasAssignmentDigest
                    ? ApprovalOptionKeys.ApproveAssignmentSessionV1Key
                    : ApprovalOptionKeys.ApproveSessionKey,
                ApprovalOptionKeys.ApproveSessionLabel)
        };

        if (profile is ApprovalOptionProfile.StandardWithDirectory)
        {
            options.Add(new ToolApprovalOption(
                hasAssignmentDigest
                    ? ApprovalOptionKeys.ApproveAssignmentAlwaysV1Key
                    : ApprovalOptionKeys.ApproveAlwaysKey,
                ApprovalOptionKeys.ApproveAlwaysLabel));
        }

        if (includeRepository)
        {
            options.Add(new ToolApprovalOption(
                hasAssignmentDigest
                    ? ApprovalOptionKeys.ApproveAssignmentRepositoryV1Key
                    : ApprovalOptionKeys.ApproveRepositoryKey,
                ApprovalOptionKeys.ApproveRepositoryLabel));
        }

        options.Add(new ToolApprovalOption(
            hasAssignmentDigest
                ? ApprovalOptionKeys.ApproveAssignmentEverywhereV1Key
                : ApprovalOptionKeys.ApproveEverywhereKey,
            ApprovalOptionKeys.LabelFor(
                hasAssignmentDigest
                    ? ApprovalOptionKeys.ApproveAssignmentEverywhereV1
                    : ApprovalOptionKeys.ApproveEverywhere,
                profile is ApprovalOptionProfile.McpTool)));
        options.Add(new ToolApprovalOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel));

        return options;
    }

    internal static bool HasAssignmentDigest(IReadOnlyList<ApprovalCandidate> candidates)
        => candidates.Any(static candidate =>
            candidate.AssignmentDigest is not null);

    private static string? ResolveOfferedRepository(
        ToolName toolName,
        bool isMessy,
        bool hasReusablePhrase,
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd)
    {
        var grantCandidates = candidates
            .Where(static candidate => !ApprovalPatternMatching.IsPureSideEffect(candidate))
            .ToArray();
        if (isMessy || !hasReusablePhrase
            || !string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal)
            || !RepositoryIdentity.TryResolveAll(
                grantCandidates.Select(static candidate => candidate.Directory).ToArray(),
                cwd,
                out var repositories))
        {
            return null;
        }

        return repositories![0].CommonDirectory;
    }

    // An approval-exempt output command is never saved, so it needs no command words.
    // An exact candidate of an unresolved command offers only "Once".
    private static bool HasReusableShellPhrase(ApprovalCandidate candidate) =>
        candidate.Shell is not null
        && candidate.Unresolved == ShellUnresolvedPart.None
        && (ApprovalPatternMatching.IsPureSideEffect(candidate)
            || candidate.VerbTokens is { Count: > 0 } tokens
               && tokens.All(static token => token.Length > 0));

    /// <summary>
    /// Returns true when the cwd is too shallow to support a folder-scoped
    /// approval grant. Mirrors the v1 minimum-depth check: a path with fewer
    /// than two non-empty segments under its root (e.g. <c>/</c>, <c>/etc/</c>,
    /// <c>C:\</c>) cannot be safely persisted as an ApprovalEntry directory.
    /// </summary>
    private static bool IsCwdTooShallow(string? cwd, ShellPathStyle pathStyle)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return false;

        return !CanonicalPath.TryGetRootDepth(cwd, pathStyle, out var depth)
               || depth < 2;
    }

    private static ToolApprovalMode GetMissingApprovalPolicyDefaultMode(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        TrustAudience audience,
        IToolApprovalMatcher matcher)
    {
        if (audience == TrustAudience.Personal && matcher.IsFailClosedOnPersonal(toolName, arguments))
            return ToolApprovalMode.Approval;

        return ToolApprovalMode.Auto;
    }

    private static ToolApprovalMode ResolveApprovalMode(
        ToolApprovalConfig? approvalPolicy,
        string approvalModeKey,
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        TrustAudience audience,
        IToolApprovalMatcher matcher)
    {
        if (approvalPolicy is null)
            return GetMissingApprovalPolicyDefaultMode(toolName, arguments, audience, matcher);

        // Matcher-derived argument-aware key (e.g. "file_write:control-plane").
        // This only fires when the matcher produced a distinct key; it is
        // unrelated to MCP tool names and therefore never consults
        // McpServerDefaults.
        if (!string.Equals(approvalModeKey, toolName.Value, StringComparison.Ordinal)
            && approvalPolicy.ToolOverrides.TryGetValue(approvalModeKey, out var matcherMode))
        {
            return matcherMode;
        }

        // No-matcher-key case shares the three-step precedence with
        // ToolApprovalConfig.GetEffectiveMode: exact ToolOverrides[toolName]
        // → McpServerDefaults[serverName] → fall-through. This keeps the
        // two callers consistent by construction.
        if (approvalPolicy.TryGetExplicitMode(toolName.Value, out var explicitMode))
            return explicitMode;

        if (audience == TrustAudience.Personal && matcher.IsFailClosedOnPersonal(toolName, arguments))
            return ToolApprovalMode.Approval;

        return approvalPolicy.DefaultMode;
    }

    private IToolApprovalMatcher SelectMatcherForTool(ToolName toolName)
    {
        if (string.Equals(toolName.Value, FileWriteTool.ToolName, StringComparison.Ordinal)
            || string.Equals(toolName.Value, FileEditTool.ToolName, StringComparison.Ordinal))
        {
            return _fileApprovalMatcher;
        }

        return DefaultApprovalMatcher.Instance;
    }

    private ShellExecutionMode ResolveShellMode()
        => _toolConfig.ShellMode ?? _defaults.ShellExecutionMode;

    private static TrustAudience ResolveAudience(ToolInvocationContext context)
        => context.Audience;

    private static bool IsShellTool(ToolRegistration registration)
        => registration.GrantCategory == "shell" || IsShellTool(registration.Tool);

    private static bool IsShellTool(INetclawTool tool)
        => string.Equals(tool.Name, ShellTool.ToolName, StringComparison.Ordinal);

    private static bool IsShellCoupledTool(INetclawTool tool)
        => IsShellTool(tool)
           || string.Equals(tool.Name, CheckBackgroundJobTool.ToolName, StringComparison.Ordinal);

    /// <summary>
    /// Returns true when the tool belongs to a subsystem whose feature flag is disabled.
    /// Disabled-subsystem tools are hidden for ALL audiences, not just Public.
    /// </summary>
    private bool IsFeatureDisabledTool(string toolName)
    {
        return toolName switch
        {
            "store_memory" or "find_memories" or "get_memories" or "update_memory"
                => !_featureGates.MemoryEnabled,
            "web_search" or "web_fetch"
                => !_featureGates.SearchEnabled,
            "skill_load" or "skill_read_resource"
                => !_featureGates.SkillSyncEnabled,
            "spawn_agent"
                => !_featureGates.SubAgentsEnabled,
            "set_reminder" or "cancel_reminder" or "list_reminders" or "get_reminder_history" or "run_reminder"
                => !_featureGates.SchedulingEnabled,
            _ => false
        };
    }

    private static string? GetToolName(AITool tool)
        => tool is AIFunction function ? function.Name : null;
}

/// <summary>
/// Subsystem feature flags consumed by <see cref="ToolAccessPolicy"/> to hide
/// tools belonging to disabled subsystems. All flags default to <c>true</c>.
/// </summary>
public sealed record FeatureGates(
    bool MemoryEnabled = true,
    bool SearchEnabled = true,
    bool SkillSyncEnabled = true,
    bool SubAgentsEnabled = true,
    bool SchedulingEnabled = true)
{
    /// <summary>All subsystems enabled — used as the default when no gates are supplied.</summary>
    public static readonly FeatureGates AllEnabled = new();
}

/// <summary>
/// Context for an approval-gated tool invocation. Contains the information
/// needed to present the approval prompt and cache the decision.
/// </summary>
public sealed record ToolApprovalContext(
    string ToolName,
    string DisplayText,
    IReadOnlyList<string> Patterns,
    // Verb-only projection of Candidates. Kept for renderers that bullet-
    // list verbs in the prompt body (Slack, Discord) without needing the
    // directory half.
    IReadOnlyList<string> CandidateVerbs,
    IReadOnlyList<ToolApprovalOption> Options,
    // Resolved cwd at the moment the gate decided approval was required.
    // Threaded through ToolInteractionRequest → PendingToolInteraction so
    // an "Always here" click persists with the actual directory rather
    // than a null sentinel (which would silently behave as "Always
    // anywhere").
    string? Cwd = null,
    // True when the invocation cannot be cleanly split into verb-chain
    // approval units (bash control-flow, unbalanced quotes/brackets).
    // Channel adapters use this to omit the persistent-grant buttons and
    // surface the "complex command" hint.
    bool IsMessy = false,
    // Per-clause (verb, directory) pairs for the persisted ApprovalEntry store.
    // The list includes path operands, redirect targets, and pipeline clauses.
    // A null directory uses Cwd. ApprovedAlways stores these effective scopes.
    IReadOnlyList<ApprovalCandidate>? Candidates = null)
{
    internal bool IsManagedTemporaryRetry { get; init; }

    internal string? ManagedTemporaryDirectory { get; init; }

    internal string? PlatformTemporaryRoot { get; init; }

    internal string? RepositoryCommonDirectory { get; init; }
}

public sealed record ToolApprovalOption(ApprovalOptionKey Key, string Label);

public sealed class ToolAccessDeniedException : InvalidOperationException
{
    public ToolAccessDeniedException(string denyReason)
        : this(denyReason, null)
    {
    }

    internal ToolAccessDeniedException(string denyReason, string? denyMessage)
        : base(denyMessage ?? denyReason)
    {
        DenyReason = denyReason;
        DenyMessage = denyMessage;
    }

    public string DenyReason { get; }

    internal string? DenyMessage { get; }

    internal string ToAgentResult() => DenyMessage ?? $"Tool access denied: {DenyReason}";
}

/// <summary>
/// Thrown by the executor when a tool invocation requires interactive user
/// approval before execution. Caught by the pipeline to initiate the
/// approval flow.
/// </summary>
public sealed class ToolApprovalRequiredException : InvalidOperationException
{
    public ToolApprovalRequiredException(ToolApprovalContext context)
        : base($"Tool '{context.ToolName}' requires approval")
    {
        ApprovalContext = context;
    }

    public ToolApprovalContext ApprovalContext { get; }
}
