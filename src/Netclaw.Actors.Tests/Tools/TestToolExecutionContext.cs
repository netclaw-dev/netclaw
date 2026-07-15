// -----------------------------------------------------------------------
// <copyright file="TestToolExecutionContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Tools;

internal static class TestToolExecutionContext
{
    public static InteractiveApprovalCapability InteractiveApproval(bool available) => available
        ? new InteractiveApprovalCapability.Available(new TestParentApprovalBridge())
        : new InteractiveApprovalCapability.Unavailable();

    public static ToolExecutionContext CreateUnbound()
        => CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Public,
        });

    public static ToolExecutionContext CreateUnbound(TestToolExecutionContextOptions options)
        => Create(new ToolSessionScope.Sessionless(), options);

    public static ToolExecutionContext CreateBound(
        string sessionId,
        string? sessionDirectory,
        TrustAudience audience)
        => new(new ToolRunScope
        {
            Session = new ToolSessionScope.Bound(sessionId, sessionDirectory),
            Audience = audience,
            InlineOutputBudget = InlineOutputBudget.Default,
            InteractiveApproval = new InteractiveApprovalCapability.Available(new TestParentApprovalBridge()),
        }, ToolExecutionTimeout.Default);

    public static ToolExecutionContext CreateBound(
        string sessionId,
        string? sessionDirectory,
        TestToolExecutionContextOptions options)
        => Create(new ToolSessionScope.Bound(sessionId, sessionDirectory), options);

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
}

internal sealed record TestToolExecutionContextOptions
{
    public required TrustAudience Audience { get; init; }
    public InlineOutputBudget InlineOutputBudget { get; init; } = InlineOutputBudget.Default;
    public TrustBoundary? Boundary { get; init; }
    public string? ChannelType { get; init; }
    public ChannelDeliveryTargetInfo? DefaultDeliveryTarget { get; init; }
    public ChannelDeliveryTargetInfo? RequestedDeliveryTarget { get; init; }
    public InteractiveApprovalCapability InteractiveApproval { get; init; }
        = new InteractiveApprovalCapability.Available(new TestParentApprovalBridge());
    public ModelModality ModelInputModalities { get; init; } = ModelModality.Text;
    public Func<object, string, CancellationToken, Task<object>>? SpawnChildActor { get; init; }
    public string? ProjectDirectory { get; init; }
    public string? InheritedCwd { get; init; }
    public IReadOnlyList<string> RecentFiles { get; init; } = [];
    public ToolExecutionTimeout ExecutionTimeout { get; init; } = ToolExecutionTimeout.Default;
    public string? Cwd { get; init; }
    public Action<SubAgentNotificationInfo>? SubAgentActivitySink { get; init; }
}

internal sealed class TestParentApprovalBridge : IParentApprovalBridge
{
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
