// -----------------------------------------------------------------------
// <copyright file="SubAgentActorTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Threading.Channels;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tests.Sessions;
using ApprovalOptionKeys = Netclaw.Actors.Protocol.ApprovalOptionKeys;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tests.Utilities;
using FakeChatClient = Netclaw.Tests.Utilities.FakeChatClient;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;


namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// Shared fixtures for <see cref="SubAgentActor"/> tests: temp directories that the
/// teardown removes, a minimal definition, and tool-result lookup. A derived class
/// owns its own actor system configuration.
/// </summary>
public abstract class SubAgentActorTestBase : TestKit, IAsyncDisposable
{
    protected static readonly TimeSpan ApprovalAskTimeout = TimeSpan.FromSeconds(30);

    // Each call owns a unique temp directory (issue #2266); teardown disposes them all.
    private readonly List<TestSessionTempDirectory> _tempDirs = [];

    protected string TestPath(string category, string name)
    {
        var dir = TestSessionTempDirectory.Create($"netclaw-subagent-test-{category}-{name}-");
        _tempDirs.Add(dir);
        return dir.Path;
    }

    // TestKit stops the actor system only after AfterAllAsync returns, and it fails
    // the test when AfterAllAsync takes more than 5 seconds. Delete the directories
    // after TestKit has disposed, and not in AfterAllAsync.
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            foreach (var dir in _tempDirs)
                await dir.DisposeAsync();
            _tempDirs.Clear();
        }
    }

    protected SubAgentActorTestBase(ITestOutputHelper output) : base(output: output) { }

    protected static SubAgentDefinition CreateDefinition(IReadOnlyList<INetclawTool>? tools = null)
    {
        return new SubAgentDefinition
        {
            Name = new AgentName("test-agent"),
            SystemPrompt = "You are a test agent.",
            Tools = tools ?? [],
            EmitStructuredFindings = false
        };
    }

    protected static string? GetLastToolResult(FakeChatClient fakeClient, string callId)
    {
        Assert.NotNull(fakeClient.LastReceivedMessages);
        return GetLastToolResult(fakeClient.LastReceivedMessages, callId);
    }

    protected static string? GetLastToolResult(
        IReadOnlyList<ChatMessage>? messages,
        string callId)
    {
        Assert.NotNull(messages);
        return messages
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single(r => r.CallId == callId)
            .Result?.ToString();
    }

    protected sealed class ProjectPromptProvider(
        string expectedProjectDirectory,
        string projectInstructions) : ISystemPromptProvider
    {
        public int CallCount { get; private set; }

        public string GetSystemPrompt(TrustAudience audience, string? projectDirectory = null)
            => string.Empty;

        public string? GetProjectInstructions(TrustAudience audience, string? projectDirectory)
        {
            CallCount++;
            return string.Equals(projectDirectory, expectedProjectDirectory, StringComparison.Ordinal)
                ? projectInstructions
                : null;
        }

        public string? GetOperatingRules(TrustAudience audience) => null;
    }
}
