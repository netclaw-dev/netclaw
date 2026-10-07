// -----------------------------------------------------------------------
// <copyright file="LongApprovalCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// The shape of a live approval prompt that Slack rejected with
/// <c>invalid_blocks</c>: a long pull request body in a heredoc inside a
/// command substitution. ShellSyntaxTree splits the source into commands, and
/// the <c>gh</c> command becomes one exact candidate whose verb is its full
/// source text, with each newline shown as <c>\n</c>.
/// </summary>
internal static class LongApprovalCommand
{
    private const string GhCommandHead = "gh api repos/netclaw-dev/skill-server/pulls --method POST";

    public static string Command { get; } = BuildCommand();

    public static string ExactGhVerb { get; } =
        Command[Command.IndexOf(GhCommandHead, StringComparison.Ordinal)..].Replace("\n", "\\n", StringComparison.Ordinal);

    public static IReadOnlyList<ToolInteractionOption> OnceOrDeny { get; } =
    [
        new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
        new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
    ];

    /// <summary>
    /// Builds the request that the session sends for the command. With
    /// <paramref name="withSiblingVerbs"/>, two other unapproved commands of
    /// the call stay in the prompt, so the builder shows a verb list.
    /// </summary>
    public static ToolInteractionRequest Request(bool withSiblingVerbs)
    {
        IReadOnlyList<string> verbs = withSiblingVerbs
            ? ["git push", ExactGhVerb, ExactGhVerb + " --paginate"]
            : [ExactGhVerb];
        return new ToolInteractionRequest
        {
            SessionId = new SessionId("C123/1791168710.638519"),
            Kind = "approval",
            CallId = new Netclaw.Tools.ToolCallId("call-long-1"),
            ToolName = new Netclaw.Tools.ToolName("shell_execute"),
            DisplayText = Command,
            RequesterSenderId = new SenderId("U123"),
            Patterns = verbs,
            CandidateVerbs = verbs,
            Cwd = "/home/user/repos/skill-server",
            Options = OnceOrDeny
        };
    }

    private static string BuildCommand()
    {
        var body = string.Join(
            "\n",
            Enumerable.Range(0, 60).Select(static i =>
                $"- Step {i}: publish the `SkillServer` as a NativeAOT binary and check `GET /health`."));
        return "cd /home/user/repos/skill-server && " + GhCommandHead
            + " -f title=\"Enable NativeAOT publishing\" -f head=\"user:spike/aot\" -f base=\"dev\""
            + " -F body=\"$(cat <<'EOF'\n## Summary\n\n" + body + "\nEOF\n)\"";
    }
}
