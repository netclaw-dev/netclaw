// -----------------------------------------------------------------------
// <copyright file="SubAgentTruncatedArtifactTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.SubAgents;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// A child reply that stops at the output limit without a tool call ends the run as failed with the reason
/// output_truncated. The result keeps only a short preview, so the runtime also keeps the whole cut-off answer
/// text in artifacts/truncated-output.md of the run folder. The file is marked as partial, and the output of the
/// run names its path (result.md prints that output unchanged). The text passes the same redaction as other
/// child output. Raw reasoning never reaches a file: a reply that holds only reasoning has no answer text to
/// keep, so the runtime writes no artifact and the run keeps its existing outcome.
/// </summary>
public sealed class SubAgentTruncatedArtifactTests(ITestOutputHelper output) : TestKit(output: output)
{
    private const string ArtifactName = "truncated-output.md";
    private const string TailMarker = "TAIL-MARKER-AFTER-THE-PREVIEW";
    private const string ReasoningMarker = "RAW-REASONING-MARKER";
    private const string SecretBody = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij";

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // SubAgentActor is standalone: no persistence or hosting needed.
    }

    [Theory]
    [InlineData("text-cut-off")]
    [InlineData("thinking-only")]
    public async Task Truncated_reply_keeps_its_answer_text_as_a_partial_artifact_and_never_its_reasoning(string row)
    {
        using var dir = new DisposableTempDir();
        var scope = SubAgentTestScope.Create(scopeId: "test-session/subagent/test/run-2679", sessionDirectory: dir.Path);
        var artifactPath = Path.Combine(
            ((ToolSessionScope.Bound)scope.Authority.Session).Storage.ArtifactDirectory.Value, ArtifactName);

        // The answer is far longer than the 300 character preview. The secret sits after the preview, so only
        // a redaction of the full text can remove it from the file.
        var answer = new string('a', 400) + " ghp_" + SecretBody + " " + TailMarker;
        var textCutOff = row == "text-cut-off";
        var client = new FakeChatClient
        {
            ResponseContentsByCall = textCutOff
                ? [[new TextReasoningContent(ReasoningMarker), new TextContent(answer)]]
                : Enumerable.Repeat<IReadOnlyList<AIContent>>([new TextReasoningContent(ReasoningMarker)], 10).ToList(),
            FinishReasonsByCall = Enumerable.Repeat<ChatFinishReason?>(ChatFinishReason.Length, 10).ToList()
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            new SubAgentDefinition
            {
                Name = new AgentName("test-agent"),
                SystemPrompt = "You are a test agent.",
                Tools = [],
                EmitStructuredFindings = false
            },
            client,
            SubAgentActorTests.PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = scope, Task = "Write the scene file", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The outcome does not change. A thinking-only reply is still retried and then ends as an empty reply.
        Assert.False(result.Success);
        Assert.Equal(SubAgentRunOutcome.Failed, result.Outcome);
        Assert.Equal(textCutOff ? "output_truncated" : "empty_final_response", result.OutcomeReason?.Value);

        // Raw reasoning is not in the output or in any file under the run folder.
        Assert.DoesNotContain(ReasoningMarker, result.Output, StringComparison.Ordinal);
        var runFolder = Path.GetDirectoryName(Path.GetDirectoryName(artifactPath))!;
        if (Directory.Exists(runFolder))
        {
            foreach (var file in Directory.EnumerateFiles(runFolder, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain(ReasoningMarker, await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        if (!textCutOff)
        {
            Assert.False(File.Exists(artifactPath), "A reply with only reasoning has no answer text to keep.");
            Assert.DoesNotContain(ArtifactName, result.Output, StringComparison.Ordinal);
            return;
        }

        Assert.True(File.Exists(artifactPath), $"The cut-off text is missing at {artifactPath}.");
        Assert.Contains(artifactPath, result.Output, StringComparison.Ordinal);

        var saved = await File.ReadAllTextAsync(artifactPath, TestContext.Current.CancellationToken);
        Assert.Contains("partial", saved, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TailMarker, saved, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretBody[..8], saved, StringComparison.Ordinal);
    }
}
