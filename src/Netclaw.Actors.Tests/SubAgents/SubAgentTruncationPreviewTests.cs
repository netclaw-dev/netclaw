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

    // The shapes below are the redactor rules that start with a word boundary. A secret at
    // the start of a line must still match, so the preview must redact before it escapes
    // the line break. All values are fake.
    [Theory]
    [InlineData("\n", "Here is the token:", "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij", "ABCDEFGHIJKLMNOPQRSTUV")]
    [InlineData("\n", "Config:", "API_KEY=supersecretvalue123", "supersecretvalue123")]
    [InlineData("\n", "Config:", "PASSWORD=hunter2hunter2", "hunter2hunter2")]
    [InlineData("\n", "key:", "sk-proj-ABCDEFGHIJKLMNOPQRSTUV", "ABCDEFGHIJKLMNOPQRSTUV")]
    [InlineData("\r\n", "Here is the token:", "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij", "ABCDEFGHIJKLMNOPQRSTUV")]
    [InlineData("\r\n", "Config:", "API_KEY=supersecretvalue123", "supersecretvalue123")]
    [InlineData("\r\n", "Config:", "PASSWORD=hunter2hunter2", "hunter2hunter2")]
    [InlineData("\r\n", "key:", "sk-proj-ABCDEFGHIJKLMNOPQRSTUV", "ABCDEFGHIJKLMNOPQRSTUV")]
    // Control: the same secret after a space always matched.
    [InlineData(" ", "Here is the token:", "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij", "ABCDEFGHIJKLMNOPQRSTUV")]
    public async Task Secret_at_the_start_of_a_line_is_not_leaked_in_the_preview(
        string separator, string lead, string secret, string secretFragment)
    {
        var result = await RunTruncatedAsync($"{lead}{separator}{secret}");

        Assert.Equal("output_truncated", result.OutcomeReason?.Value);
        Assert.DoesNotContain(secretFragment, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_escapes_line_breaks_after_it_redacts()
    {
        var result = await RunTruncatedAsync("first line\r\nsecond line");

        Assert.Contains("first line\\nsecond line", result.Output, StringComparison.Ordinal);
    }

    // PreviewForLog feeds log sinks that can leave the host, so it needs the same order.
    [Theory]
    [InlineData("\n", "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij", "ABCDEFGHIJKLMNOPQRSTUV")]
    [InlineData("\r\n", "API_KEY=supersecretvalue123", "supersecretvalue123")]
    [InlineData("\n", "PASSWORD=hunter2hunter2", "hunter2hunter2")]
    [InlineData("\n", "sk-proj-ABCDEFGHIJKLMNOPQRSTUV", "ABCDEFGHIJKLMNOPQRSTUV")]
    public void Log_preview_does_not_leak_a_secret_at_the_start_of_a_line(
        string separator, string secret, string secretFragment)
    {
        var preview = SubAgentActor.PreviewForLog($"prefix{separator}{secret}", 300);

        Assert.DoesNotContain(secretFragment, preview, StringComparison.Ordinal);
        Assert.Contains("prefix\\n", preview, StringComparison.Ordinal);
    }

    // The key sits at the end of a line and its value is on the next line. Only the pass
    // that runs after the line break escape can match it, because the escaped "\n" then
    // joins the key to its value.
    [Theory]
    [InlineData("API_KEY=\nsupersecretvalue123", "supersecretvalue123")]
    [InlineData("API_KEY=\r\nsupersecretvalue123", "supersecretvalue123")]
    [InlineData("PASSWORD=\nhunter2hunter2", "hunter2hunter2")]
    public void Log_preview_masks_a_value_on_the_line_after_its_key(string text, string secretFragment)
    {
        var preview = SubAgentActor.PreviewForLog($"cfg {text}", 300);

        Assert.DoesNotContain(secretFragment, preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_output_masks_a_value_on_the_line_after_its_key()
    {
        var result = await RunTruncatedAsync("cfg API_KEY=\nsupersecretvalue123");

        Assert.Equal("output_truncated", result.OutcomeReason?.Value);
        Assert.DoesNotContain("supersecretvalue123", result.Output, StringComparison.Ordinal);
    }

    private async Task<SubAgentResult> RunTruncatedAsync(string cutOffText)
    {
        // The run saves the cut-off text under the session folder, so the folder must be a temp folder.
        using var dir = new DisposableTempDir();
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
            new RunSubAgent { Scope = SubAgentTestScope.Create(sessionDirectory: dir.Path), Task = "Write the scene file", Timeout = TimeSpan.FromSeconds(5) },
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
