// -----------------------------------------------------------------------
// <copyright file="TestToolExecutionContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Tools;

/// <summary>
/// Creates tool execution contexts with explicit test authority and session state.
/// </summary>
internal static class TestToolExecutionContext
{
    internal static InteractiveApprovalCapability InteractiveApproval(bool available) => available
        ? new InteractiveApprovalCapability.Available(new TestParentApprovalBridge())
        : new InteractiveApprovalCapability.Unavailable();

    internal static ToolExecutionContext CreateUnbound()
        => CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Public,
        });

    internal static ToolExecutionContext CreateUnbound(TestToolExecutionContextOptions options)
        => Create(new ToolSessionScope.Sessionless(), options);

    internal static ToolExecutionContext CreateUnboundWithoutApproval(
        TrustAudience audience = TrustAudience.Public,
        string? channelType = null)
        => CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = audience,
            ChannelType = channelType,
            InteractiveApproval = new InteractiveApprovalCapability.Unavailable(),
        });

    internal static ToolExecutionContext CreateBound(
        string sessionId,
        string? sessionDirectory,
        TrustAudience audience)
        => CreateBound(
            sessionId,
            sessionDirectory,
            new TestToolExecutionContextOptions { Audience = audience });

    internal static ToolExecutionContext CreateBound(
        string sessionId,
        string? sessionDirectory,
        TestToolExecutionContextOptions options)
        => Create(CreateSessionScope(sessionId, sessionDirectory), options);

    internal static ToolExecutionContext CreateBoundWithoutApproval(
        string sessionId,
        string? sessionDirectory,
        TrustAudience audience,
        string? channelType = null,
        ChannelDeliveryTargetInfo? requestedDeliveryTarget = null)
        => CreateBound(
            sessionId,
            sessionDirectory,
            new TestToolExecutionContextOptions
            {
                Audience = audience,
                ChannelType = channelType,
                RequestedDeliveryTarget = requestedDeliveryTarget,
                InteractiveApproval = new InteractiveApprovalCapability.Unavailable(),
            });

    internal static ToolExecutionContext CreateBoundWithStorage(
        string sessionId,
        SessionStoragePaths storage,
        TestToolExecutionContextOptions options)
        => Create(new ToolSessionScope.Bound(sessionId, storage), options);

    private static ToolExecutionContext Create(ToolSessionScope session, TestToolExecutionContextOptions options)
    {
        var outputs = options.SubAgentActivitySink is null
            ? new ToolExecutionOutputs()
            : new ToolExecutionOutputs(options.SubAgentActivitySink);
        var context = new ToolExecutionContext(new ToolRunScope
        {
            Session = session,
            Audience = options.Audience,
            InlineOutputBudget = options.InlineOutputBudget,
            Boundary = options.Boundary,
            ChannelType = options.ChannelType,
            DefaultDeliveryTarget = options.DefaultDeliveryTarget,
            RequestedDeliveryTarget = options.RequestedDeliveryTarget,
            InteractiveApproval = options.InteractiveApproval,
            ModelInputModalities = options.ModelInputModalities,
            SpawnChildActor = options.SpawnChildActor,
            ProjectDirectory = options.ProjectDirectory,
            InheritedCwd = options.InheritedCwd,
            RecentFiles = options.RecentFiles,
        }, options.ExecutionTimeout, outputs);

        if (options.Cwd is not null)
            context.Approval.SetCwd(options.Cwd);

        return context;
    }

    private static ToolSessionScope CreateSessionScope(string sessionId, string? sessionDirectory)
    {
        var resolvedSessionDirectory = sessionDirectory is not null
            && !sessionDirectory.Any(char.IsControl)
            && Path.IsPathFullyQualified(sessionDirectory)
                ? Path.GetFullPath(sessionDirectory)
                : Path.Combine(AppContext.BaseDirectory, "netclaw-test-session-workspace");

        var storage = SessionStoragePaths.CreateLegacy(
            resolvedSessionDirectory,
            Path.Combine(AppContext.BaseDirectory, "netclaw-test-session-logs"),
            "test-session");
        return new ToolSessionScope.Bound(sessionId, storage);
    }
}

/// <summary>
/// Specifies the authority, routing, and run state for a test tool invocation.
/// </summary>
internal sealed record TestToolExecutionContextOptions
{
    internal required TrustAudience Audience { get; init; }
    internal InlineOutputBudget InlineOutputBudget { get; init; } = InlineOutputBudget.Default;
    internal TrustBoundary? Boundary { get; init; }
    internal string? ChannelType { get; init; }
    internal ChannelDeliveryTargetInfo? DefaultDeliveryTarget { get; init; }
    internal ChannelDeliveryTargetInfo? RequestedDeliveryTarget { get; init; }
    internal InteractiveApprovalCapability InteractiveApproval { get; init; }
        = new InteractiveApprovalCapability.Available(new TestParentApprovalBridge());
    internal ModelModality ModelInputModalities { get; init; } = ModelModality.Text;
    internal Func<object, string, CancellationToken, Task<object>>? SpawnChildActor { get; init; }
    internal string? ProjectDirectory { get; init; }
    internal string? InheritedCwd { get; init; }
    internal IReadOnlyList<string> RecentFiles { get; init; } = [];
    internal ToolExecutionTimeout ExecutionTimeout { get; init; } = ToolExecutionTimeout.Default;
    internal string? Cwd { get; init; }
    internal Action<SubAgentNotificationInfo>? SubAgentActivitySink { get; init; }
}

/// <summary>
/// Fails tests that unexpectedly request approval through the parent bridge.
/// </summary>
internal sealed class TestParentApprovalBridge : IParentApprovalBridge
{
    /// <inheritdoc />
    public Task<ParentApprovalDecision> RequestApprovalAsync(
        ToolCallId callId,
        string toolName,
        string displayText,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> candidateVerbs,
        IReadOnlyList<ParentApprovalCandidate> candidates,
        string? cwd,
        IReadOnlyList<ParentApprovalOption> options,
        bool isMessy,
        CancellationToken ct) =>
        throw new InvalidOperationException("This test context does not service parent approval requests.");
}
