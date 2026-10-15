// -----------------------------------------------------------------------
// <copyright file="SubAgentTruncationPreviewTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.SubAgents;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// A cut-off sub-agent reply puts a 300 character preview of the cut-off text into the
/// failed outcome. The parent model and the user read that preview. The preview must
/// follow two rules: redaction sees the whole text before the cut, and the cut never
/// splits a surrogate pair.
/// </summary>
public sealed class SubAgentTruncationPreviewTests(ITestOutputHelper output) : TestKit(output: output)
{
    private const int PreviewChars = 300;
    private const string SecretPrefix = "ghp_";

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // SubAgentActor is standalone: no persistence or hosting needed.
    }

    [Fact]
    public async Task Secret_that_straddles_the_cut_is_not_leaked_in_the_preview()
    {
        // A GitHub token is "ghp_" and 20 or more letters and digits. This one has 36.
        // It starts at char 280, so only "ghp_" and 16 letters sit before the cut. That
        // prefix alone is too short for the redactor to match.
        const string secretBody = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij";
        var text = new string('a', 279) + " " + SecretPrefix + secretBody + " and the text goes on";
        Assert.Equal(280, text.IndexOf(SecretPrefix, StringComparison.Ordinal));

        var result = await RunTruncatedAsync(text);

        Assert.Equal("output_truncated", result.OutcomeReason?.Value);
        Assert.DoesNotContain(SecretPrefix, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(secretBody[..8], result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cut_inside_a_surrogate_pair_does_not_leave_a_lone_surrogate()
    {
        // Char 299 is the high surrogate and char 300 is the low surrogate of one emoji.
        var text = new string('b', 299) + "\U0001F600" + " and the text goes on";
        Assert.True(char.IsHighSurrogate(text[PreviewChars - 1]));
        Assert.True(char.IsLowSurrogate(text[PreviewChars]));

        var result = await RunTruncatedAsync(text);

        Assert.Equal("output_truncated", result.OutcomeReason?.Value);
        Assert.True(IsValidUtf16(result.Output), "The failed output holds a lone surrogate.");
    }

    private async Task<SubAgentResult> RunTruncatedAsync(string cutOffText)
    {
        var client = new FakeChatClient
        {
            ResponseContentsByCall = [[new TextContent(cutOffText)]],
            FinishReasonsByCall = [ChatFinishReason.Length]
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

        return await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Write the scene file", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // The strict encoder throws on a lone surrogate, as a JSON or Slack writer would.
    private static bool IsValidUtf16(string value)
    {
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
